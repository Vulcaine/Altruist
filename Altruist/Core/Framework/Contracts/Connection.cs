/*
Copyright 2025 Aron Gere

Licensed under the Apache License, Version 2.0 (the "License");
you may not use this file except in compliance with the License.
You may obtain a copy of the License at

    http://www.apache.org/licenses/LICENSE-2.0

Unless required by applicable law or agreed to in writing, software
distributed under the License is distributed on an "AS IS" BASIS,
WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
See the License for the specific language governing permissions and
limitations under the License.
*/

using System.Text.Json.Serialization;

using Altruist.Security;

namespace Altruist;

/// <summary>
/// One client connection on the server side (a WebSocket, TCP or UDP peer), as stored in the
/// <see cref="IConnectionStore"/>. Transports subclass <see cref="AltruistConnection"/>; application code normally
/// looks connections up (<see cref="IConnectionStore.GetConnectionAsync"/>) rather than implementing this.
/// </summary>
/// <remarks>
/// To send, prefer <see cref="ClientSender"/> / <see cref="IAltruistRouter"/> (codec, outbound queue, metrics)
/// over <see cref="SendAsync"/> on the connection, which writes raw bytes to the socket.
/// </remarks>
public interface IAltruistConnection : IStoredModel
{
    /// <summary>Authentication details attached at connect time; <c>null</c> for anonymous connections.</summary>
    AuthDetails? AuthDetails { get; }
    /// <summary>The portal route (path) the client connected on; used to match portals' connect/disconnect hooks.</summary>
    public string Route { get; set; }
    /// <summary>The connection id (client id) used as key in the store and by every send API.</summary>
    string ConnectionId { get; }
    /// <summary>Assigns the connection id (trimmed) and makes it the storage id.</summary>
    /// <param name="connectionId">The new id.</param>
    /// <exception cref="ArgumentException">The id is null, empty or whitespace.</exception>
    void SetId(string connectionId);
    /// <summary>The peer's remote address as reported by the transport.</summary>
    string RemoteAddress { get; }
    /// <summary>When the connection was accepted (UTC).</summary>
    DateTime ConnectedAt { get; }
    /// <summary>
    /// Writes one already-encoded binary message to the socket. Low level: prefer <see cref="ClientSender"/>,
    /// which encodes packets, honours the outbound queue mode and records metrics.
    /// </summary>
    /// <param name="data">The encoded message bytes.</param>
    Task SendAsync(byte[] data);
    /// <summary>Receives one complete message. Called by the connection manager's read loop; not for application code.</summary>
    /// <param name="cancellationToken">Cancels the receive (the read loop uses it for the idle timeout).</param>
    /// <returns>The received message bytes.</returns>
    Task<byte[]> ReceiveAsync(CancellationToken cancellationToken);
    /// <summary>Closes the connection gracefully. To disconnect a client and run the portals' disconnect hooks use <see cref="IConnectionManager.DisconnectAsync"/>.</summary>
    Task CloseAsync();

    /// <summary><c>true</c> when <see cref="AuthDetails"/> is present and not expired.</summary>
    bool Authenticated => AuthDetails != null && AuthDetails.IsAlive();

    /// <summary>Whether the underlying socket is open. Senders skip connections that are not connected.</summary>
    bool IsConnected { get; }
}

/// <summary>
/// Base class for transport connections (WebSocket, TCP, UDP). The I/O members throw
/// <see cref="NotImplementedException"/> here and are overridden by each transport. Serialized to JSON when stored
/// remotely (auth details and route are not serialized).
/// </summary>
public class AltruistConnection : StoredModel, IAltruistConnection
{
    private string _connectionId = string.Empty;

    /// <inheritdoc/>
    [JsonIgnore]
    public AuthDetails? AuthDetails { get; set; }

    /// <inheritdoc/>
    [JsonIgnore]
    public string Route { get; set; } = "";

    /// <inheritdoc/>
    [JsonPropertyName("connectedAt")]
    public DateTime ConnectedAt { get; set; }

    /// <inheritdoc/>
    [JsonPropertyName("remoteAddress")]
    public string RemoteAddress { get; set; } = string.Empty;

    /// <summary>The runtime type name of the connection (setter ignored; kept for serialization).</summary>
    [JsonPropertyName("type")]
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public override string Type { get => GetType().Name; set { } }

    /// <summary>
    /// The transport's config name (<c>websocket</c>, <c>tcp</c>, <c>udp</c>): selects the per-transport codec
    /// (<c>altruist:server:transport:{mode}:codec:provider</c>, see <see cref="ICodecResolver.ResolveForConnection"/>).
    /// Null for connections of no particular transport (they use the global codec). Transports override it.
    /// </summary>
    [JsonIgnore]
    public virtual string? TransportMode => null;

    /// <summary>The connection id. Setting it (e.g. on deserialization) allows empty; use <see cref="SetId"/> to validate.</summary>
    [JsonPropertyName("connectionId")]
    public string ConnectionId
    {
        get => _connectionId;
        set => SetIdCore(value, allowEmpty: true);
    }

    /// <inheritdoc/>
    [JsonPropertyName("isConnected")]
    public virtual bool IsConnected { get; set; }

    /// <summary>Last activity time (UTC); initialized at construction.</summary>
    [JsonPropertyName("lastActivity")]
    public DateTime LastActivity { get; set; } = DateTime.UtcNow;
    /// <summary>Storage key; a random GUID until <see cref="SetId"/> assigns the connection id.</summary>
    public override string StorageId { get; set; } = Guid.NewGuid().ToString();

    /// <inheritdoc/>
    public void SetId(string connectionId)
        => SetIdCore(connectionId, allowEmpty: false);

    private void SetIdCore(string connectionId, bool allowEmpty)
    {
        if (string.IsNullOrWhiteSpace(connectionId))
        {
            if (allowEmpty)
            {
                _connectionId = string.Empty;
                return;
            }

            throw new ArgumentException("Connection id cannot be empty.", nameof(connectionId));
        }

        _connectionId = connectionId.Trim();
        StorageId = _connectionId;
    }

    /// <summary>Half-closes the connection (sends a close frame, keeps reading). Implemented by transports; throws <see cref="NotImplementedException"/> here.</summary>
    public virtual Task CloseOutputAsync()
    {
        throw new NotImplementedException();
    }

    /// <summary>
    /// Tears the connection down at once, without a close handshake and without waiting for a
    /// pending send (e.g. a peer that stopped reading). The read loop then ends and the normal
    /// disconnect path runs. Transports without an abort do nothing.
    /// </summary>
    public virtual void Abort() { }

    /// <inheritdoc/>
    public virtual Task CloseAsync()
    {
        throw new NotImplementedException();
    }

    /// <inheritdoc/>
    public virtual Task<byte[]> ReceiveAsync(CancellationToken cancellationToken)
    {
        throw new NotImplementedException();
    }

    /// <inheritdoc/>
    public virtual Task SendAsync(byte[] data)
    {
        throw new NotImplementedException();
    }

    /// <summary>
    /// Sends one binary message from a buffer the caller may reuse once the task completes.
    /// Transports without a native overload copy it.
    /// </summary>
    public virtual Task SendAsync(ReadOnlyMemory<byte> data) => SendAsync(data.ToArray());
}

/// <summary>
/// Client side of a transport: connects out to a gateway (server-to-server or test clients). Currently not used
/// by the framework's own services.
/// </summary>
public interface ITransportClient
{
    /// <summary>Connects to the gateway.</summary>
    /// <param name="gatewayUrl">The gateway URL, e.g. <c>ws://host:port/route</c>.</param>
    Task ConnectAsync(string gatewayUrl);
    /// <summary>Closes the connection.</summary>
    Task DisconnectAsync();
    /// <summary>Sends one encoded message.</summary>
    /// <param name="data">The encoded message bytes.</param>
    Task SendAsync(byte[] data);
    /// <summary>Receives one complete message.</summary>
    /// <param name="cancellationToken">Cancels the receive.</param>
    /// <returns>The received message bytes.</returns>
    Task<byte[]> ReceiveAsync(CancellationToken cancellationToken);
    /// <summary>Whether the client is connected.</summary>
    bool IsConnected { get; }
}

/// <summary>
/// Owns the server side of every client connection: runs the read loop, dispatches packets to portal gates through
/// the interceptors, fires portal connect/disconnect hooks, and exposes the connection/room registry (delegating
/// to <see cref="ISocketManager"/> / <see cref="IConnectionStore"/>). Registered as a singleton when
/// <c>altruist:server:transport</c> is configured.
/// </summary>
/// <remarks>
/// Inject it mainly to disconnect clients (<see cref="DisconnectAsync"/> / <see cref="DisconnectEngineAwareAsync"/>)
/// or to add interceptors at runtime. For connection/room lookups prefer <see cref="ISocketManager"/>; to send
/// packets use <see cref="IAltruistRouter"/>.
/// </remarks>
public interface IConnectionManager
{
    /// <summary>Returns every stored connection whose route matches the portal's route (trailing slashes ignored).</summary>
    /// <param name="portal">The portal whose clients to list.</param>
    Task<IEnumerable<AltruistConnection>> GetConnectionsForPortal(IPortal portal);
    /// <summary>
    /// Transport entry point for a newly accepted connection: runs portal <c>OnConnectingAsync</c> hooks, stores the
    /// connection in the waiting room, runs <c>OnConnectedAsync</c>, then reads and dispatches packets until the client
    /// disconnects, errors or is idle longer than <c>altruist:server:transport:timeout</c> (seconds, default 10), and
    /// finally disconnects it. The task completes only when the connection is gone. Called by transports, not by
    /// application code.
    /// </summary>
    /// <param name="socket">The accepted connection.</param>
    /// <param name="event">The route/event the connection arrived on (used for metrics and interceptor context).</param>
    /// <param name="clientId">The client id to use when the connection has none assigned yet.</param>
    Task HandleConnection(AltruistConnection socket, string @event, string clientId);
    /// <summary>
    /// Decodes one packet and dispatches it to the portal gate registered for <c>packet.Event</c>, after all
    /// interceptors have run (an interceptor may reject it). Used by the read loop and transports.
    /// </summary>
    /// <param name="packet">The packet header (its <c>Event</c> selects the gate).</param>
    /// <param name="bytes">The raw message bytes to decode into the gate's packet type.</param>
    /// <param name="event">The transport route/event, for metrics and interceptor context.</param>
    /// <param name="clientId">The sending client's id.</param>
    /// <returns><c>false</c> when the packet has no event or could not be decoded (the read loop then stops); otherwise <c>true</c>, including when rejected or unhandled.</returns>
    Task<bool> ProcessPacket(AltruistPacket packet, byte[] bytes, string @event, string clientId);
    /// <summary>
    /// Adds an interceptor that runs on every inbound packet before its handler. Interceptors registered in DI as
    /// <see cref="IInterceptor"/> are added automatically; adding the same instance twice is a no-op.
    /// </summary>
    /// <param name="interceptor">The interceptor to add.</param>
    /// <exception cref="ArgumentNullException"><paramref name="interceptor"/> is null.</exception>
    void AddInterceptor(IInterceptor interceptor);
    /// <summary>Removes a connection from the store without closing it or running disconnect hooks. To disconnect a client use <see cref="DisconnectAsync"/>.</summary>
    /// <param name="connectionId">The connection id.</param>
    Task RemoveConnectionAsync(string connectionId);
    /// <inheritdoc cref="IConnectionStore.AddConnectionAsync"/>
    Task<bool> AddConnectionAsync(string connectionId, AltruistConnection socket, string? roomId = null);
    /// <inheritdoc cref="IConnectionStore.GetConnectionAsync"/>
    Task<AltruistConnection?> GetConnectionAsync(string connectionId);
    /// <inheritdoc cref="IConnectionStore.GetAllConnectionIdsAsync"/>
    Task<IEnumerable<string>> GetAllConnectionIdsAsync();
    /// <summary>
    /// Like <see cref="DisconnectAsync"/>, but when a game engine is running the disconnect is queued as an engine task
    /// (runs on the engine's next tick) so it does not race the simulation; without an engine it disconnects at once.
    /// Use this from gameplay code that may run concurrently with the tick loop.
    /// </summary>
    /// <param name="clientId">The client to disconnect.</param>
    Task DisconnectEngineAwareAsync(string clientId);
    /// <summary>
    /// Disconnects a client now: closes the socket, runs every portal's <c>OnDisconnectedAsync</c>, drops its outbound
    /// queue and per-client interceptor state, and removes it (and its room membership) from the store.
    /// </summary>
    /// <param name="clientId">The client to disconnect.</param>
    Task DisconnectAsync(string clientId);
    /// <inheritdoc cref="IConnectionStore.GetAllConnectionsDictAsync"/>
    Task<Dictionary<string, AltruistConnection>> GetAllConnectionsDictAsync();
    /// <inheritdoc cref="IConnectionStore.GetAllConnectionsAsync"/>
    Task<ICursor<AltruistConnection>> GetAllConnectionsAsync();
    /// <inheritdoc cref="IConnectionStore.GetConnectionsInRoomAsync"/>
    Task<Dictionary<string, AltruistConnection>> GetConnectionsInRoomAsync(string roomId);
    /// <inheritdoc cref="IConnectionStore.FindAvailableRoomAsync"/>
    Task<RoomPacket?> FindAvailableRoomAsync();
    /// <inheritdoc cref="IConnectionStore.FindRoomForClientAsync"/>
    Task<RoomPacket?> FindRoomForClientAsync(string clientId);
    /// <inheritdoc cref="IConnectionStore.CreateRoomAsync"/>
    Task<RoomPacket> CreateRoomAsync(string? roomId);
    /// <summary>Deletes a room record (members are not disconnected or moved).</summary>
    /// <param name="roomName">The room id.</param>
    Task DeleteRoomAsync(string roomName);
    /// <inheritdoc cref="IConnectionStore.GetRoomAsync"/>
    Task<RoomPacket?> GetRoomAsync(string roomId);
    /// <inheritdoc cref="IConnectionStore.GetAllRoomsAsync"/>
    Task<Dictionary<string, RoomPacket>> GetAllRoomsAsync();
    /// <inheritdoc cref="IConnectionStore.JoinRoomAsync"/>
    Task<RoomPacket?> JoinRoomAsync(string connectionId, string roomId);
    /// <inheritdoc cref="IConnectionStore.SaveRoomAsync"/>
    Task SaveRoomAsync(RoomPacket room);
    /// <summary>No-op in the default manager (store cleanup runs on disconnect); kept for custom managers.</summary>
    Task Cleanup();
    /// <inheritdoc cref="IConnectionStore.IsConnectionExistsAsync"/>
    Task<bool> IsConnectionExistsAsync(string connectionId);
}
