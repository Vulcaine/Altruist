using System.Collections.Concurrent;
using System.Linq.Expressions;
using System.Reflection;

namespace Altruist.Client;

/// <summary>
/// Routes inbound server frames to <see cref="PacketAttribute"/>-decorated
/// handler methods. Mirrors the server's <c>CombatEventDispatcher</c> shape:
/// assembly scan → per-method <c>Expression.Lambda</c> compile → registry
/// keyed off the packet's auto-detected <see cref="IPacketBase.MessageCode"/>.
///
/// <para>The wire shape is the standard <see cref="MessageEnvelope"/> fixarray(3)
/// — element [0] is the MessageCode, element [2] is the inner packet bytes. We
/// peek the MC via <see cref="MessageEnvelopeShape"/> (no MessagePack on the
/// envelope), look up registered handlers, then call the codec's
/// <c>Deserialize&lt;T&gt;</c> with the slice and invoke every handler.</para>
///
/// <para>The codec is supplied <b>per-call</b> rather than at construction —
/// different transports may use different codecs (e.g. TCP+messagepack,
/// WS+JSON), and the same dispatcher serves all of them.</para>
///
/// <para>Multiple <see cref="PacketAttribute"/> methods MAY register for the
/// same packet type; all of them fire on dispatch. Order is undefined — if the
/// game ever needs ordering we add an <c>Order</c> property to the attribute.</para>
///
/// <para>Errors are caught and routed through <see cref="Logger"/> so one bad
/// packet or buggy handler doesn't crash the receive loop. Logger is null by
/// default (silent).</para>
///
/// <para><b>Wire format:</b> a codec implementing <see cref="IClientEnvelopeCodec"/> (such as <see cref="JsonClientCodec"/>)
/// reads the envelope itself; for every other codec it is parsed as MessagePack (<see cref="MessageEnvelopeShape"/>).
/// Only the inner packet goes through the codec's <c>Deserialize&lt;T&gt;</c>.</para>
///
/// <para><b>Threading:</b> handlers run synchronously on whatever thread calls
/// <see cref="Dispatch"/> — a background pump task when the router auto-pumps, or the
/// caller of <see cref="IAltruistClientRouter.DrainInbound"/> otherwise.
/// <see cref="Register"/> is safe to call concurrently with dispatch.</para>
///
/// <para><b>Lifetime:</b> DI singleton (<c>[Service]</c>). Handler classes marked
/// <see cref="PacketHandlerAttribute"/> are discovered at boot by <see cref="ClientPacketHandlerConfig"/> and registered
/// from the application provider on first use (<see cref="RegisterDiscoveredHandlers"/>); call <see cref="Register"/> yourself for
/// handlers built outside DI or when constructing the dispatcher manually.</para>
/// </summary>
/// <example>
/// <code>
/// var dispatcher = new ClientPacketDispatcher { Logger = Console.WriteLine };
/// dispatcher.Register(new ChatHandlers());              // methods marked [Packet(typeof(X))]
/// dispatcher.Dispatch(frameBytes, new MessagePackClientCodec());
/// </code>
/// </example>
[Service]
public sealed class ClientPacketDispatcher
{
    private readonly ConcurrentDictionary<uint, HandlerEntry[]> _handlers = new();
    private readonly ConcurrentDictionary<Type, Func<IClientCodec, byte[], object?>> _deserializers = new();
    private static readonly ConcurrentDictionary<Type, uint> _codeCache = new();
    private readonly IServiceProvider? _services;
    private readonly object _discoveryLock = new();
    private volatile bool _discoveredRegistered;

    /// <summary>A dispatcher without discovered handlers: register handlers yourself with <see cref="Register"/>.</summary>
    public ClientPacketDispatcher() { }

    /// <summary>
    /// The DI constructor: the <see cref="PacketHandlerAttribute"/> classes found by <see cref="ClientPacketHandlerConfig"/>
    /// are resolved from <paramref name="services"/> and registered on first use (<see cref="RegisterDiscoveredHandlers"/>).
    /// </summary>
    /// <param name="services">The application's service provider.</param>
    [Microsoft.Extensions.DependencyInjection.ActivatorUtilitiesConstructor]
    public ClientPacketDispatcher(IServiceProvider services)
    {
        _services = services ?? throw new ArgumentNullException(nameof(services));
    }

    /// <summary>
    /// Resolves and registers the <see cref="PacketHandlerAttribute"/> classes discovered at boot
    /// (<see cref="DiscoveredPacketHandlers"/>), once. Called by <see cref="IAltruistClientRouter.ConnectAsync"/> and by the
    /// first <see cref="Dispatch"/>; a dispatcher built without a service provider has nothing to register.
    /// </summary>
    /// <exception cref="InvalidOperationException">A discovered handler cannot be constructed from DI or has an invalid
    /// <see cref="PacketAttribute"/> method (see <see cref="Register"/>).</exception>
    public void RegisterDiscoveredHandlers()
    {
        if (_discoveredRegistered || _services is null) return;
        lock (_discoveryLock)
        {
            if (_discoveredRegistered) return;
            var discovered = (DiscoveredPacketHandlers?)_services.GetService(typeof(DiscoveredPacketHandlers));
            foreach (var handlerType in discovered?.Types ?? Array.Empty<Type>())
            {
                var instance = _services.GetService(handlerType) ?? throw new InvalidOperationException(
                    $"[PacketHandler] {handlerType.FullName} is not registered in DI.");
                Register(instance);
            }
            _discoveredRegistered = true;
        }
    }

    private static bool TryReadEnvelope(byte[] frame, IClientCodec codec, out uint messageCode, out byte[] message)
    {
        if (codec is IClientEnvelopeCodec envelopeCodec)
            return envelopeCodec.TryReadEnvelope(frame, out messageCode, out message);

        messageCode = MessageEnvelopeShape.PeekMessageCode(frame);
        message = messageCode == 0 ? Array.Empty<byte>() : MessageEnvelopeShape.ExtractMessage(frame);
        return messageCode != 0;
    }

    /// <summary>Optional sink for diagnostic / error log lines.</summary>
    public Action<string>? Logger { get; set; }

    /// <summary>
    /// Scan <paramref name="handlerInstance"/> for <see cref="PacketAttribute"/>
    /// methods, compile invokers, and register each under the packet's
    /// auto-detected MessageCode. The instance is captured by the compiled
    /// lambda; it must outlive the dispatcher.
    /// </summary>
    /// <remarks>
    /// Does not check for <see cref="PacketHandlerAttribute"/>; any object with
    /// <see cref="PacketAttribute"/> instance methods is accepted. Registering the same
    /// instance twice makes its handlers fire twice. When several handlers share a code, the
    /// packet is deserialized once, as the type of the first registered handler.
    /// </remarks>
    /// <param name="handlerInstance">Object whose <see cref="PacketAttribute"/> methods to register.</param>
    /// <exception cref="ArgumentNullException"><paramref name="handlerInstance"/> is <c>null</c>.</exception>
    /// <exception cref="InvalidOperationException">A <c>[Packet]</c> method does not take exactly one
    /// parameter of the attribute's packet type, does not return <c>void</c>, or the MessageCode
    /// cannot be resolved (no public parameterless constructor, not an <see cref="IPacketBase"/>,
    /// or default MessageCode 0 with no <see cref="PacketAttribute.MessageCode"/> override).
    /// Handlers registered before the failing method stay registered.</exception>
    public void Register(object handlerInstance)
    {
        if (handlerInstance is null) throw new ArgumentNullException(nameof(handlerInstance));

        var type = handlerInstance.GetType();
        var methodsWithAttr = TypeDiscovery.FindInstanceMethodsWithAttribute<PacketAttribute>(type);

        foreach (var (method, attr) in methodsWithAttr)
        {
            var pars = method.GetParameters();
            if (pars.Length != 1)
                throw new InvalidOperationException(
                    $"Method {type.Name}.{method.Name} marked with [Packet] must take exactly one parameter (the deserialized packet).");

            var packetType = pars[0].ParameterType;
            if (packetType != attr.PacketType)
                throw new InvalidOperationException(
                    $"Method {type.Name}.{method.Name} parameter type '{packetType.Name}' does not match [Packet(typeof({attr.PacketType.Name}))].");

            if (method.ReturnType != typeof(void))
                throw new InvalidOperationException(
                    $"Method {type.Name}.{method.Name} marked with [Packet] must return void.");

            // Explicit MC on the attribute wins (use this for receive-only types
            // that don't initialize MessageCode in their default ctor — common
            // pattern for server-to-client packets where the server stamps the MC).
            // Otherwise auto-detect from a default instance.
            var mc = attr.MessageCode != 0
                ? attr.MessageCode
                : ResolveMessageCode(packetType);
            var invoker = BuildInvoker(handlerInstance, method, packetType);
            _deserializers.GetOrAdd(packetType, BuildDeserializer);

            var entry = new HandlerEntry(type, method, packetType, invoker);
            _handlers.AddOrUpdate(mc,
                _ => new[] { entry },
                (_, existing) =>
                {
                    var combined = new HandlerEntry[existing.Length + 1];
                    Array.Copy(existing, combined, existing.Length);
                    combined[existing.Length] = entry;
                    return combined;
                });
        }
    }

    /// <summary>
    /// Dispatch one wire frame: peek the MessageCode, deserialize the inner
    /// packet bytes via <paramref name="codec"/> as the registered concrete
    /// type, invoke every matching handler. Caught exceptions go through
    /// <see cref="Logger"/>.
    /// </summary>
    /// <remarks>
    /// Silently returns for empty frames, code 0, unregistered codes, malformed envelopes,
    /// deserialization failures and <c>null</c> packets (each logged through <see cref="Logger"/>).
    /// A throwing handler does not stop the remaining handlers.
    /// </remarks>
    /// <param name="envelopeBytes">One complete <see cref="MessageEnvelope"/> frame
    /// (TCP length prefix already stripped).</param>
    /// <param name="codec">Codec used to deserialize the inner packet.</param>
    /// <exception cref="ArgumentNullException"><paramref name="codec"/> is <c>null</c> (and the frame is non-empty).</exception>
    public void Dispatch(byte[] envelopeBytes, IClientCodec codec)
    {
        if (envelopeBytes is null || envelopeBytes.Length == 0) return;
        if (codec is null) throw new ArgumentNullException(nameof(codec));
        RegisterDiscoveredHandlers();

        if (!TryReadEnvelope(envelopeBytes, codec, out var mc, out var inner))
        {
            Logger?.Invoke($"[Altruist.Client] Unrecognised payload (len={envelopeBytes.Length})");
            return;
        }

        if (!_handlers.TryGetValue(mc, out var snapshot) || snapshot.Length == 0)
        {
            Logger?.Invoke($"[Altruist.Client] Unhandled MC={mc} (len={envelopeBytes.Length})");
            return;
        }

        if (inner.Length == 0)
        {
            Logger?.Invoke($"[Altruist.Client] Empty inner payload MC={mc}");
            return;
        }

        var packetType = snapshot[0].PacketType;
        if (!_deserializers.TryGetValue(packetType, out var deserializer))
        {
            // Shouldn't happen — Register builds it. Defensive.
            Logger?.Invoke($"[Altruist.Client] No deserializer for packetType={packetType.Name} (MC={mc})");
            return;
        }

        object? packet;
        try { packet = deserializer(codec, inner); }
        catch (Exception ex)
        {
            Logger?.Invoke($"[Altruist.Client] Deserialize fail MC={mc} as {packetType.Name}: {ex.GetType().Name}: {ex.Message}");
            return;
        }

        if (packet is null) return;

        for (int i = 0; i < snapshot.Length; i++)
        {
            try { snapshot[i].Invoker(packet); }
            catch (Exception ex)
            {
                Logger?.Invoke(
                    $"[Altruist.Client] Handler {snapshot[i].HandlerType.Name}.{snapshot[i].Method.Name} " +
                    $"failed for MC={mc}: {ex.GetType().Name}: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Number of registered handlers for the given MessageCode. Used by tests
    /// to assert registration succeeded.
    /// </summary>
    /// <param name="mc">MessageCode to look up.</param>
    /// <returns>Handler count, 0 when none.</returns>
    public int HandlerCountForMC(uint mc)
    {
        RegisterDiscoveredHandlers();
        return _handlers.TryGetValue(mc, out var list) ? list.Length : 0;
    }

    private static uint ResolveMessageCode(Type packetType) =>
        _codeCache.GetOrAdd(packetType, static t =>
        {
            object? instance;
            try { instance = Activator.CreateInstance(t); }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    $"Cannot resolve MessageCode for {t.FullName}: type must have a public parameterless constructor.", ex);
            }
            if (instance is not IPacketBase pb)
                throw new InvalidOperationException(
                    $"Cannot resolve MessageCode for {t.FullName}: type must implement IPacketBase.");
            if (pb.MessageCode == 0)
                throw new InvalidOperationException(
                    $"Cannot resolve MessageCode for {t.FullName}: default-instance MessageCode is 0. " +
                    $"Set it as a property initializer (e.g., 'public uint MessageCode {{ get; set; }} = 1252;').");
            return pb.MessageCode;
        });

    private static Action<object> BuildInvoker(object target, MethodInfo method, Type packetType)
    {
        var arg = Expression.Parameter(typeof(object), "packet");
        var castArg = Expression.Convert(arg, packetType);
        var call = Expression.Call(Expression.Constant(target), method, castArg);
        return Expression.Lambda<Action<object>>(call, arg).Compile();
    }

    private static Func<IClientCodec, byte[], object?> BuildDeserializer(Type packetType)
    {
        var codec = Expression.Parameter(typeof(IClientCodec), "codec");
        var bytes = Expression.Parameter(typeof(byte[]), "bytes");

        var deserializeMethod = typeof(IClientCodec)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .First(m =>
                m.Name == nameof(IClientCodec.Deserialize) &&
                m.IsGenericMethodDefinition &&
                m.GetParameters().Length == 1 &&
                m.GetParameters()[0].ParameterType == typeof(byte[]))
            .MakeGenericMethod(packetType);

        var call = Expression.Call(codec, deserializeMethod, bytes);
        var asObject = Expression.Convert(call, typeof(object));
        return Expression.Lambda<Func<IClientCodec, byte[], object?>>(asObject, codec, bytes).Compile();
    }

    private sealed record HandlerEntry(
        Type HandlerType,
        MethodInfo Method,
        Type PacketType,
        Action<object> Invoker);
}
