namespace Altruist;

/// <summary>
/// Marker contract for a portal: a class that groups realtime packet handlers (<see cref="GateAttribute"/> methods)
/// and connection lifecycle hooks for one transport route.
/// </summary>
/// <remarks>
/// Prefer deriving from <see cref="Portal"/> rather than implementing this directly. Portals are discovered by
/// <see cref="PortalAttribute"/> (see <see cref="Altruist.Web.Features.PortalDiscovery"/>), registered in DI
/// automatically (no <c>[Service]</c> needed; constructor dependencies are injected), resolved once at startup
/// and then reused for the process lifetime, so treat portal state as shared across all clients.
/// </remarks>
public interface IPortal
{
    /// <summary>
    /// Normalized route this portal serves (e.g. <c>/game</c>). Set by the framework from
    /// <see cref="PortalAttribute.Endpoint"/> when the portal is created; connections are matched to portals by
    /// comparing this with the connection's route (trailing slashes ignored). Do not set it yourself.
    /// </summary>
    public string Route { get; set; }
}

/// <summary>
/// Base class for realtime packet handlers. Annotate the subclass with <see cref="PortalAttribute"/> to bind it to
/// a route, add <see cref="GateAttribute"/> methods to handle incoming packet events, and optionally implement
/// <see cref="OnConnectingAsync"/>, <see cref="OnConnectedAsync"/> and <see cref="OnDisconnectedAsync"/> to
/// observe the connection lifecycle.
/// </summary>
/// <remarks>
/// A <c>[Gate]</c> method must return <see cref="Task"/> or <see cref="Task{TResult}"/> and take either no
/// parameters, <c>(string clientId)</c>, or <c>(TPacket packet, string clientId)</c> where <c>TPacket</c>
/// implements <see cref="IPacket"/>; other shapes throw at startup. Gate event names are global across all
/// portals. For HTTP endpoints use a regular ASP.NET controller instead.
/// </remarks>
/// <example>
/// <code>
/// [Portal("/chat")]
/// public class ChatPortal : Portal, OnConnectedAsync
/// {
///     private readonly IAltruistRouter _router;
///     public ChatPortal(IAltruistRouter router) =&gt; _router = router;
///
///     [Gate("chat-message")]
///     public Task OnMessage(ChatMessagePacket packet, string clientId) =&gt; /* handle */ Task.CompletedTask;
///
///     public Task OnConnectedAsync(string clientId, ConnectionManager connectionManager, AltruistConnection connection)
///         =&gt; Task.CompletedTask;
/// }
/// </code>
/// </example>
public abstract class Portal : IPortal
{
    /// <inheritdoc/>
    public string Route { get; set; } = "";
}


/// <summary>
/// Optional portal hook invoked when a client connects to the portal's route, before the connection is stored
/// (it is not yet in any room and not yet reachable by broadcasts). Use it to inspect or reject early;
/// for work that needs the connection registered (sending packets, joining rooms) use <see cref="OnConnectedAsync"/>.
/// </summary>
/// <remarks>
/// Exceptions are logged and swallowed; they do not abort the connection.
/// </remarks>
public interface OnConnectingAsync
{
    /// <summary>Called for every portal on the connection's route, in sequence, before the connection is stored.</summary>
    /// <param name="clientId">Provisional client id; the final id is the connection's <c>ConnectionId</c> when it is set.</param>
    /// <param name="connectionManager">The connection manager handling this connection.</param>
    /// <param name="connection">The new transport connection.</param>
    public Task OnConnectingAsync(string clientId, ConnectionManager connectionManager, AltruistConnection connection);
}


/// <summary>
/// Optional portal hook invoked after a client's connection has been stored (in the waiting room) and before
/// its read loop starts. Use it to send a welcome/handshake packet or set up per-client state; to run before the
/// connection is registered use <see cref="OnConnectingAsync"/>.
/// </summary>
/// <remarks>
/// Exceptions are logged and swallowed. Packets from the client are not processed until all portals' hooks return.
/// </remarks>
public interface OnConnectedAsync
{
    /// <summary>Called for every portal on the connection's route, in sequence, once the connection is registered.</summary>
    /// <param name="clientId">Final client id (the connection id).</param>
    /// <param name="connectionManager">The connection manager handling this connection.</param>
    /// <param name="connection">The registered transport connection.</param>
    public Task OnConnectedAsync(string clientId, ConnectionManager connectionManager, AltruistConnection connection);
}


/// <summary>
/// Optional portal hook invoked when a client disconnects (graceful close, idle timeout, read error, or an explicit
/// <c>ConnectionManager.DisconnectAsync</c>). Use it to release per-client state.
/// </summary>
/// <remarks>
/// Exceptions are logged and swallowed. An explicit disconnect notifies every registered portal, not only the
/// portals on the client's route, so implementations should tolerate unknown client ids.
/// </remarks>
public interface OnDisconnectedAsync
{
    /// <summary>Called for each portal when the client's connection ends. After an explicit disconnect the read loop's cleanup can call it a second time for the same client, so keep it idempotent.</summary>
    /// <param name="clientId">The client that disconnected.</param>
    /// <param name="exception">The failure that ended the connection (e.g. <see cref="TimeoutException"/> on idle timeout), or <c>null</c> for a clean close.</param>
    public Task OnDisconnectedAsync(string clientId, Exception? exception);
}
