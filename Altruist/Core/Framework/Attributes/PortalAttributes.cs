namespace Altruist;


/// <summary>
/// Binds a <see cref="Portal"/> (realtime packet handler class) to a transport route. Portals are discovered by
/// this attribute at startup, registered in DI automatically (no <c>[Service]</c> needed) and their
/// <see cref="GateAttribute"/> methods are wired to incoming packet events.
/// </summary>
/// <remarks>
/// <para>
/// Use a portal for realtime (WebSocket/TCP/UDP) traffic; for HTTP endpoints use a regular ASP.NET controller.
/// The route is used as given for WebSocket matching (include any prefix you want in the path) and is
/// normalized with <see cref="PathUtils.NormalizeRoute"/> into <see cref="IPortal.Route"/>.
/// The class must be concrete and non-abstract; blank endpoints are ignored. Not inherited by subclasses.
/// </para>
/// <para>
/// Use exactly one <c>[Portal]</c> per class: <c>AllowMultiple</c> is true and discovery yields one route per
/// attribute, but portal creation reads a single attribute to set <see cref="IPortal.Route"/> and fails on duplicates.
/// </para>
/// </remarks>
/// <example>
/// <code>
/// [Portal("/chat")]
/// public class ChatPortal : Portal
/// {
///     [Gate("chat-message")]
///     public Task OnMessage(ChatMessagePacket packet, string clientId) =&gt; Task.CompletedTask;
/// }
/// </code>
/// </example>
[AttributeUsage(AttributeTargets.Class, Inherited = false, AllowMultiple = true)]
public sealed class PortalAttribute : Attribute
{
    /// <summary>Route path the portal serves, e.g. <c>"/game"</c>.</summary>
    public string Endpoint { get; }
    /// <summary>
    /// Optional free-form context label. Only surfaced in the dashboard's service listing; it does not
    /// affect routing.
    /// </summary>
    public string? Context { get; }

    /// <summary>Binds the portal to <paramref name="endpoint"/>.</summary>
    /// <param name="endpoint">Route path, e.g. <c>"/game"</c>.</param>
    /// <param name="context">Optional descriptive label (see <see cref="Context"/>).</param>
    public PortalAttribute(string endpoint, string? context = "")
    {
        Endpoint = endpoint;
        Context = context;
    }
}

/// <summary>
/// Marks a portal method as the handler for an incoming packet event. Gate methods are registered when the
/// framework warms up the portals at startup (see <see cref="PortalGateRegistry{TMarker}"/>).
/// </summary>
/// <remarks>
/// <para>
/// Allowed signatures (any accessibility, instance methods on a <see cref="PortalAttribute"/> class):
/// <c>Task M()</c>, <c>Task M(string clientId)</c>, or <c>Task M(TPacket packet, string clientId)</c> where
/// <c>TPacket</c> implements <see cref="IPacket"/>; <c>Task&lt;T&gt;</c> returns are allowed. Any other shape
/// throws <see cref="InvalidOperationException"/> at startup.
/// </para>
/// <para>
/// Event names are global across all portals. If two gates use the same event name, packet dispatch uses
/// only the most recently registered one, so keep names unique. Use one <c>[Gate]</c> per method: although
/// <c>AllowMultiple</c> is true, registration reads a single attribute and fails on duplicates.
/// </para>
/// </remarks>
/// <example>
/// <code>
/// [Gate("join")]
/// public Task OnJoin(JoinPacket packet, string clientId) =&gt; _rooms.JoinAsync(clientId, packet.RoomId);
/// </code>
/// </example>
[AttributeUsage(AttributeTargets.Method, Inherited = false, AllowMultiple = true)]
public sealed class GateAttribute : Attribute
{
    /// <summary>Packet event name this method handles (matched ordinally against the incoming packet's event).</summary>
    public string Event { get; }

    /// <summary>Marks the method as the handler for <paramref name="eventName"/>.</summary>
    /// <param name="eventName">Packet event name, e.g. <c>"join"</c>.</param>
    public GateAttribute(string eventName)
    {
        Event = eventName;
    }
}
