using System.Collections.Concurrent;
using System.Linq.Expressions;
using System.Reflection;

namespace Altruist.Client;

/// <summary>
/// Routes inbound server frames to <see cref="PacketAttribute"/>-decorated
/// handler methods. Mirrors the server's <c>CombatEventDispatcher</c> shape:
/// assembly scan → per-method <see cref="Expression.Lambda"/> compile → registry
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
/// </summary>
[Service]
public sealed class ClientPacketDispatcher
{
    private readonly ConcurrentDictionary<uint, HandlerEntry[]> _handlers = new();
    private readonly ConcurrentDictionary<Type, Func<IClientCodec, byte[], object?>> _deserializers = new();
    private static readonly ConcurrentDictionary<Type, uint> _codeCache = new();

    /// <summary>Optional sink for diagnostic / error log lines.</summary>
    public Action<string>? Logger { get; set; }

    /// <summary>
    /// Scan <paramref name="handlerInstance"/> for <see cref="PacketAttribute"/>
    /// methods, compile invokers, and register each under the packet's
    /// auto-detected MessageCode. The instance is captured by the compiled
    /// lambda; it must outlive the dispatcher.
    /// </summary>
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
    public void Dispatch(byte[] envelopeBytes, IClientCodec codec)
    {
        if (envelopeBytes is null || envelopeBytes.Length == 0) return;
        if (codec is null) throw new ArgumentNullException(nameof(codec));

        uint mc;
        try { mc = MessageEnvelopeShape.PeekMessageCode(envelopeBytes); }
        catch (Exception ex)
        {
            Logger?.Invoke($"[Altruist.Client] PeekMessageCode failed: {ex.Message}");
            return;
        }

        if (mc == 0)
        {
            Logger?.Invoke($"[Altruist.Client] Unrecognised payload (len={envelopeBytes.Length})");
            return;
        }

        if (!_handlers.TryGetValue(mc, out var snapshot) || snapshot.Length == 0)
        {
            Logger?.Invoke($"[Altruist.Client] Unhandled MC={mc} (len={envelopeBytes.Length})");
            return;
        }

        byte[] inner;
        try { inner = MessageEnvelopeShape.ExtractMessage(envelopeBytes); }
        catch (Exception ex)
        {
            Logger?.Invoke($"[Altruist.Client] ExtractMessage MC={mc} failed: {ex.Message}");
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
    public int HandlerCountForMC(uint mc) =>
        _handlers.TryGetValue(mc, out var list) ? list.Length : 0;

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
