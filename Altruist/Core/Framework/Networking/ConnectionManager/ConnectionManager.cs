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

using Altruist.Engine;

using Microsoft.Extensions.Logging;

using System.Diagnostics;
using System.Text;

namespace Altruist
{

    struct DisconnectToken
    {

    }

    /// <summary>
    /// Default <see cref="IConnectionManager"/> (singleton, registered when <c>altruist:server:transport</c> is configured).
    /// Transports hand every accepted connection to <see cref="HandleConnection"/>, which runs the portal lifecycle hooks,
    /// the read loop, interceptors and gate dispatch, and the disconnect path. Connection/room queries delegate to
    /// <see cref="ISocketManager"/>.
    /// </summary>
    /// <remarks>
    /// <list type="bullet">
    /// <item>Lifecycle: <c>OnConnectingAsync</c> → registration in the waiting room → <c>OnConnectedAsync</c> → read loop →
    /// <c>OnDisconnectedAsync</c> → outbound queue and <see cref="IConnectionStateInterceptor"/> state forgotten → removal from the store.
    /// Only portals whose route matches the connection's route receive these hooks.</item>
    /// <item>Idle timeout: a connection that sends nothing for <c>altruist:server:transport:timeout</c> seconds (default 10) is closed.</item>
    /// <item>Interceptors: every DI-registered <see cref="IInterceptor"/> plus any added with <see cref="AddInterceptor"/>.</item>
    /// </list>
    /// Inject <see cref="IConnectionManager"/> for connection/room lookups from portals; to send data use the outbound senders.
    /// </remarks>
    [Service(typeof(IConnectionManager))]
    [ConditionalOnConfig("altruist:server:transport")]
    public class ConnectionManager : IConnectionManager
    {
        private readonly ICodecResolver _codecResolver;
        private readonly ICodec _defaultCodec;
        private readonly List<IInterceptor> _interceptors = new();
        private readonly ISocketManager _socketManager;
        private readonly IEngineCore? _engine;
        private readonly ILogger _logger;
        private readonly IDashboardNetworkRecorder? _networkRecorder;
        private readonly OutboundQueues? _outbound;

        private readonly int _idleTimeout;
        private readonly Task _waitingRoom;
        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, ConnectionSession> _sessions = new();

        private static readonly System.Collections.Concurrent.ConcurrentDictionary<System.Reflection.MethodInfo, System.Reflection.ParameterInfo[]> ParameterCache = new();

        /// <summary>DI constructor. Also starts creating the waiting room, which every connection awaits before joining it.</summary>
        /// <param name="socketManager">Connection/room registry.</param>
        /// <param name="codecResolver">Resolves the default codec used to decode packets.</param>
        /// <param name="loggerFactory">Logger factory.</param>
        /// <param name="engineCore">Optional engine; when present <see cref="DisconnectEngineAwareAsync"/> runs on the engine thread.</param>
        /// <param name="networkRecorder">Optional dashboard packet recorder.</param>
        /// <param name="timeout">Idle timeout in seconds (<c>altruist:server:transport:timeout</c>, default 10).</param>
        /// <param name="interceptors">Interceptors registered in DI (<c>[Service(typeof(IInterceptor))]</c>); applied to every portal.</param>
        /// <param name="outbound">Outbound queues, forgotten per client on disconnect.</param>
        public ConnectionManager(
            ISocketManager socketManager,
            ICodecResolver codecResolver,
            ILoggerFactory loggerFactory, IEngineCore? engineCore = null,
            IDashboardNetworkRecorder? networkRecorder = null,
            [AppConfigValue("altruist:server:transport:timeout", "10")] int timeout = 10,
            IEnumerable<IInterceptor>? interceptors = null,
            OutboundQueues? outbound = null
         )
        {
            _socketManager = socketManager;
            _codecResolver = codecResolver;
            _defaultCodec = codecResolver.Resolve();
            _engine = engineCore;
            _logger = loggerFactory.CreateLogger(GetType());
            _networkRecorder = networkRecorder;
            _idleTimeout = timeout;
            _outbound = outbound;

            // Interceptors registered in DI ([Service(typeof(IInterceptor))]) apply to every portal.
            if (interceptors is not null)
                foreach (var interceptor in interceptors)
                    AddInterceptor(interceptor);

            // Started here, awaited before the first connection joins it (it used to be fire-and-forget:
            // a connection could arrive before the room existed, and a failure went unnoticed).
            _waitingRoom = CreateRoomAsync(StoreConstants.WaitingRoomId);
        }


        /// <summary>
        /// Adds an interceptor that runs before gate handlers. Prefer registering it in DI with <c>[Service(typeof(IInterceptor))]</c>;
        /// use this for manual setup. Adding the same instance twice is a no-op. Not thread-safe: call during startup.
        /// </summary>
        /// <param name="interceptor">Interceptor to add.</param>
        /// <exception cref="ArgumentNullException"><paramref name="interceptor"/> is null.</exception>
        public void AddInterceptor(IInterceptor interceptor)
        {
            if (interceptor is null)
                throw new ArgumentNullException(nameof(interceptor));
            // The same instance registered in DI and added by hand runs once.
            if (!_interceptors.Contains(interceptor))
                _interceptors.Add(interceptor);
        }

        /// <summary>Returns all connections whose route equals the portal's route (trailing slashes ignored). Scans every connection.</summary>
        /// <param name="portal">Portal whose route to match.</param>
        public async Task<IEnumerable<AltruistConnection>> GetConnectionsForPortal(IPortal portal)
        {
            var allConns = await GetAllConnectionsAsync();
            var connections = new List<AltruistConnection>();

            foreach (var conn in allConns)
            {
                if (conn.Route.TrimEnd('/') == portal.Route.TrimEnd('/'))
                {
                    connections.Add(conn);
                }
            }

            return connections;
        }

        /// <summary>
        /// Dispatches one decoded packet: resolves the gate handler for <c>packet.Event</c>, decodes the payload into the
        /// handler's packet parameter type with the global codec, runs interceptors (a rejection skips the handler), sets
        /// <see cref="PacketContext"/> (raw bytes and, for <see cref="ILagCompensated"/> packets, the client tick) and
        /// invokes the handler. Called by the read loop (with the connection's per-transport codec); call it directly only
        /// for custom transports/relays.
        /// </summary>
        /// <remarks>
        /// Supported handler shapes: <c>()</c>, <c>(string clientId)</c>, <c>(TPacket packet, string clientId)</c>.
        /// Interceptors run for every packet: handlers of any shape (with a null packet for the first two), unknown events
        /// and undecodable payloads (null packet). An undecodable payload is dropped; the connection stays open.
        /// Exceptions thrown by the handler propagate (and end the read loop).
        /// </remarks>
        /// <param name="packet">Envelope carrying the event name.</param>
        /// <param name="bytes">Payload bytes to decode into the handler's packet type.</param>
        /// <param name="event">Route (portal path) of the connection.</param>
        /// <param name="clientId">Connection id of the sender.</param>
        /// <returns><c>false</c> when the packet has no event (the read loop then closes the connection); otherwise <c>true</c>, including for rejected, unknown and undecodable packets.</returns>
        public Task<bool> ProcessPacket(AltruistPacket packet, byte[] bytes, string @event, string clientId) =>
            ProcessPacketAsync(_defaultCodec, packet, bytes, @event, clientId);

        private async Task<bool> ProcessPacketAsync(ICodec codec, AltruistPacket packet, byte[] bytes, string @event, string clientId)
        {
            if (string.IsNullOrEmpty(packet.Event))
                return false;

            var totalStart = Stopwatch.GetTimestamp();
            var decodeMs = 0d;
            var handlerMs = 0d;
            string? error = null;
            IPacket? message = null;
            string? packetType = null;
            string? portalName = null;

            if (PortalGateRegistry<IPortal>.TryGetHandler(packet.Event, out var @delegate))
            {
                var data = bytes;
                var context = new InterceptContext(packet.Event, clientId, data.Length, @event);
                var handlerMethod = @delegate.Method;
                var parameters = ParameterCache.GetOrAdd(handlerMethod, static m => m.GetParameters());
                var hasPacketPayload = parameters.Length >= 2 && typeof(IPacket).IsAssignableFrom(parameters[0].ParameterType);
                var parameterType = hasPacketPayload ? parameters[0].ParameterType : null;
                packetType = parameterType?.Name;
                portalName = handlerMethod.DeclaringType?.Name ?? @delegate.Target?.GetType().Name;

                try
                {
                    var decodeStart = Stopwatch.GetTimestamp();
                    if (hasPacketPayload && parameterType is not null && data.Length > 0)
                    {
                        message = codec.Decoder.Decode<IPacket>(data, parameterType);
                    }
                    else if (hasPacketPayload && parameterType is not null)
                    {
                        message = (IPacket?)Activator.CreateInstance(parameterType);
                    }
                    decodeMs = Stopwatch.GetElapsedTime(decodeStart).TotalMilliseconds;
                }
                catch (Exception decodeEx)
                {
                    decodeMs = 0;
                    // Client input: logged at debug only. The packet is dropped (a default-constructed packet never
                    // reaches the handler) and the connection stays open; the interceptors still see it, like an
                    // unknown event, so a client cannot spam undecodable payloads past the rate limits.
                    _logger.LogDebug("Failed to decode {Len} bytes as {Type}: {Error}", data.Length, parameterType?.Name ?? "empty", decodeEx.Message);
                    error = decodeEx.Message;
                    await RunInterceptorsAsync(context, null);
                    await RecordPacketAsync(
                        connectionId: clientId,
                        route: @event,
                        packet: packet,
                        payload: null,
                        rawPayload: data,
                        portalName: portalName,
                        packetType: packetType,
                        durationMs: Stopwatch.GetElapsedTime(totalStart).TotalMilliseconds,
                        decodeMs: null,
                        handlerMs: null,
                        error: error);
                    return true;
                }

                // Interceptors finish before the handler runs so they can veto the packet (rate limits, size caps),
                // whatever the handler's shape: a () or (clientId) gate has no packet object but is still rate-limited.
                if (await RunInterceptorsAsync(context, message))
                    return true;

                PacketContext.Set(data);

                // Auto-detect lag-compensated packets and set client tick
                if (message is ILagCompensated lagCompensated && lagCompensated.ClientTick > 0)
                    PacketContext.SetClientTick(lagCompensated.ClientTick);

                try
                {
                    var handlerStart = Stopwatch.GetTimestamp();
                    Task? handlerTask = parameters.Length switch
                    {
                        0 => (Task?)@delegate.DynamicInvoke(),
                        1 when parameters[0].ParameterType == typeof(string) => (Task?)@delegate.DynamicInvoke(clientId),
                        _ => (Task?)@delegate.DynamicInvoke(message, clientId)
                    };

                    if (handlerTask != null)
                    {
                        await handlerTask;
                    }

                    handlerMs = Stopwatch.GetElapsedTime(handlerStart).TotalMilliseconds;
                }
                catch (Exception ex)
                {
                    error = ex.InnerException?.Message ?? ex.Message;
                    throw;
                }
                finally
                {
                    PacketContext.Clear();
                    await RecordPacketAsync(
                        connectionId: clientId,
                        route: @event,
                        packet: packet,
                        payload: message,
                        rawPayload: data,
                        portalName: portalName,
                        packetType: packetType,
                        durationMs: Stopwatch.GetElapsedTime(totalStart).TotalMilliseconds,
                        decodeMs: decodeMs,
                        handlerMs: handlerMs,
                        error: error);
                }
            }
            else
            {
                // Unknown events still pass the interceptors (rate limits) so a client cannot spam
                // them for free. The name is client-chosen: sanitized and logged at debug only.
                await RunInterceptorsAsync(new InterceptContext(packet.Event, clientId, bytes.Length, @event), null);
                _logger.LogDebug("No handler found for event: {Event}", SanitizeForLog(packet.Event));
                await RecordPacketAsync(
                    connectionId: clientId,
                    route: @event,
                    packet: packet,
                    payload: null,
                    rawPayload: bytes,
                    portalName: null,
                    packetType: null,
                    durationMs: Stopwatch.GetElapsedTime(totalStart).TotalMilliseconds,
                    decodeMs: null,
                    handlerMs: null,
                    error: "No handler found.");
            }

            return true;
        }

        // Returns true when an interceptor rejected the packet. A single interceptor is awaited directly (no array/WhenAll).
        private async Task<bool> RunInterceptorsAsync(InterceptContext context, IPacket? message)
        {
            if (_interceptors.Count == 0)
                return false;
            if (_interceptors.Count == 1)
            {
                await _interceptors[0].Intercept(context, message!);
                return context.Rejected;
            }
            var tasks = new Task[_interceptors.Count];
            for (int i = 0; i < _interceptors.Count; i++)
                tasks[i] = _interceptors[i].Intercept(context, message!);
            await Task.WhenAll(tasks);
            return context.Rejected;
        }

        /// <summary>Printable ASCII only, at most 64 chars (event names come from clients).</summary>
        private static string SanitizeForLog(string value)
        {
            var span = value.AsSpan(0, Math.Min(value.Length, 64));
            var sb = new StringBuilder(span.Length);
            foreach (var c in span)
                sb.Append(c is >= ' ' and <= '~' ? c : '?');
            return sb.ToString();
        }

        // Recording is off unless the dashboard captures packets; skip the async machinery then.
        private Task RecordPacketAsync(
            string connectionId,
            string route,
            AltruistPacket packet,
            object? payload,
            byte[]? rawPayload,
            string? portalName,
            string? packetType,
            double durationMs,
            double? decodeMs,
            double? handlerMs,
            string? error)
            => _networkRecorder is null || !_networkRecorder.CapturePackets
                ? Task.CompletedTask
                : RecordPacketCoreAsync(connectionId, route, packet, payload, rawPayload, portalName, packetType, durationMs, decodeMs, handlerMs, error);

        private async Task RecordPacketCoreAsync(
            string connectionId,
            string route,
            AltruistPacket packet,
            object? payload,
            byte[]? rawPayload,
            string? portalName,
            string? packetType,
            double durationMs,
            double? decodeMs,
            double? handlerMs,
            string? error)
        {
            if (_networkRecorder is null || !_networkRecorder.CapturePackets)
                return;

            string? roomId = null;
            try
            {
                roomId = (await FindRoomForClientAsync(connectionId))?.Id;
            }
            catch
            {
                roomId = null;
            }

            await _networkRecorder.RecordAsync(new DashboardNetworkEvent
            {
                Kind = "packet",
                Direction = "inbound",
                Transport = "socket",
                Route = route,
                Portal = portalName,
                Gate = packet.Event,
                Event = packet.Event,
                PacketType = packetType,
                ConnectionId = connectionId,
                ClientId = connectionId,
                RoomId = roomId,
                DurationMs = durationMs,
                DecodeDurationMs = decodeMs,
                HandlerDurationMs = handlerMs,
                Error = error
            }, payload, rawPayload);
        }

        /// <summary>
        /// Owns an accepted connection until it closes: runs <c>OnConnectingAsync</c>, registers it in the waiting room, runs
        /// <c>OnConnectedAsync</c>, then reads and dispatches packets until the peer closes, the idle timeout fires or an
        /// error occurs, and finally runs the disconnect path. Called by transports; awaits for the whole connection lifetime.
        /// </summary>
        /// <remarks>Codecs implementing <c>IFramedCodec</c> use a stream read loop with the codec's framer (raw TCP); others use one message per receive (JSON <c>{"event","data"}</c> or a 1-byte-length event-name prefix for binary codecs).</remarks>
        /// <param name="connection">The accepted connection.</param>
        /// <param name="event">Route (portal path) the connection arrived on.</param>
        /// <param name="clientId">Proposed client id; replaced by <c>connection.ConnectionId</c> when that is set (e.g. by <c>OnConnectingAsync</c>).</param>
        public async Task HandleConnection(AltruistConnection connection, string @event, string clientId)
        {
            var portals = PortalGateRegistry<IPortal>.GetAllHandlers()
                .Where(portal => portal.Route.TrimEnd('/') == connection.Route.TrimEnd('/'))
                .ToArray();

            foreach (var portal in portals)
            {
                try
                {
                    if (portal is OnConnectingAsync connectingAsync)
                    {
                        await connectingAsync.OnConnectingAsync(clientId, this, connection);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "OnConnectingAsync handler threw for client {ClientId}.", clientId);
                }
            }

            clientId = string.IsNullOrWhiteSpace(connection.ConnectionId)
                ? clientId
                : connection.ConnectionId;

            await _waitingRoom;
            var session = new ConnectionSession(portals);
            _sessions[clientId] = session;
            await _socketManager.AddConnectionAsync(clientId, connection, StoreConstants.WaitingRoomId);

            foreach (var portal in portals)
            {
                try
                {
                    if (portal is OnConnectedAsync connectedAsync)
                    {
                        await connectedAsync.OnConnectedAsync(clientId, this, connection);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "OnConnectedAsync handler threw for client {ClientId}.", clientId);
                }
            }

            Exception? failureException = null;
            var idleTimeout = TimeSpan.FromSeconds(_idleTimeout);
            var codec = _codecResolver.ResolveForConnection(connection);

            try
            {
                if (codec is IFramedCodec framedCodec)
                    await RunFramedReadLoop(connection, codec, framedCodec.Framer, @event, clientId, idleTimeout);
                else
                    await RunStandardReadLoop(connection, codec, @event, clientId, idleTimeout);
            }
            catch (TimeoutException tex)
            {
                failureException = tex;
                _logger.LogInformation(
                    "Closing idle connection for client {ClientId} after {Timeout} inactivity.",
                    clientId, idleTimeout);
            }
            catch (Exception ex)
            {
                failureException = ex;
                _logger.LogError(ex, "Error handling connection for client {ClientId}.", clientId);
            }
            finally
            {
                try
                {
                    await DisconnectSessionAsync(clientId, session, failureException);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "OnDisconnectedAsync handler threw for client {ClientId}.", clientId);
                }
            }
        }

        /// <summary>
        /// Standard read loop for message-framed transports (WebSocket, MessagePack).
        /// Each ReceiveAsync call returns exactly one complete message.
        /// </summary>
        private async Task RunStandardReadLoop(
            AltruistConnection connection, ICodec codec, string @event, string clientId, TimeSpan idleTimeout)
        {
            // One timeout source per connection, re-armed per message (a new source plus timer per
            // message was a large share of the per-packet allocations at 60 inputs/s).
            using var cts = new CancellationTokenSource();
            bool isJsonCodec = codec.GetType().Name.Contains("Json", StringComparison.OrdinalIgnoreCase);
            while (true)
            {
                byte[] packetData;

                if (!cts.TryReset())
                    throw new TimeoutException($"Connection idle for {idleTimeout.TotalSeconds} seconds.");
                cts.CancelAfter(idleTimeout);
                try
                {
                    packetData = await connection.ReceiveAsync(cts.Token);
                }
                catch (OperationCanceledException)
                {
                    throw new TimeoutException($"Connection idle for {idleTimeout.TotalSeconds} seconds.");
                }

                if (packetData.Length == 0)
                    break;

                // Determine framing strategy:
                // - JSON codec: data is a full JSON object like {"event":"hello","data":{...}}
                //   → decode directly with the codec (AltruistPacket has [JsonPropertyName("event")])
                // - Binary codecs (MessagePack): use event-prefixed framing
                //   → [1-byte eventLen][eventName UTF8][payload]
                AltruistPacket packet;
                byte[] payloadBytes;

                if (!isJsonCodec && packetData.Length > 2 && packetData[0] > 0 && packetData[0] < 128)
                {
                    int eventLen = packetData[0];
                    if (eventLen + 1 <= packetData.Length)
                    {
                        var eventName = System.Text.Encoding.UTF8.GetString(packetData, 1, eventLen);
                        payloadBytes = packetData.AsSpan(1 + eventLen).ToArray();
                        packet = new AltruistPacket { Event = eventName, MessageCode = PacketCodes.Altruist };
                    }
                    else
                    {
                        packet = codec.Decoder.Decode<AltruistPacket>(packetData);
                        payloadBytes = packetData;
                    }
                }
                else
                {
                    packet = codec.Decoder.Decode<AltruistPacket>(packetData);

                    // For JSON: extract the "data" field as the payload for gate handlers.
                    // Client sends: {"event":"hello","data":{"text":"Hi!"}}
                    // The gate handler needs just the data portion, not the envelope.
                    if (isJsonCodec)
                    {
                        try
                        {
                            using var jsonDoc = System.Text.Json.JsonDocument.Parse(packetData);
                            if (jsonDoc.RootElement.TryGetProperty("data", out var dataElement))
                            {
                                payloadBytes = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(dataElement);
                            }
                            else
                            {
                                // No "data" wrapper — use the full object as payload
                                payloadBytes = packetData;
                            }
                        }
                        catch
                        {
                            payloadBytes = packetData;
                        }
                    }
                    else
                    {
                        payloadBytes = packetData;
                    }
                }

                if (!await ProcessPacketAsync(codec, packet, payloadBytes, @event, clientId))
                    break;
            }
        }

        /// <summary>
        /// Framed read loop for raw stream protocols (e.g. binary TCP).
        /// Buffers incoming bytes and uses the IPacketFramer to extract complete packets.
        /// Multiple packets per read are processed; partial packets are carried over.
        /// </summary>
        private async Task RunFramedReadLoop(
            AltruistConnection connection, ICodec codec, IPacketFramer framer, string @event, string clientId, TimeSpan idleTimeout)
        {
            // Use a growable buffer backed by ArrayPool to avoid per-receive allocations
            int accLength = 0;
            byte[] accBuffer = System.Buffers.ArrayPool<byte>.Shared.Rent(4096);

            try
            {
                while (true)
                {
                    byte[] received;

                    using var cts = new CancellationTokenSource(idleTimeout);
                    try
                    {
                        received = await connection.ReceiveAsync(cts.Token);
                    }
                    catch (OperationCanceledException)
                    {
                        throw new TimeoutException($"Connection idle for {idleTimeout.TotalSeconds} seconds.");
                    }

                    if (received.Length == 0)
                        break;

                    // Append received data to accumulator buffer
                    int needed = accLength + received.Length;
                    if (needed > accBuffer.Length)
                    {
                        var newBuf = System.Buffers.ArrayPool<byte>.Shared.Rent(needed * 2);
                        Buffer.BlockCopy(accBuffer, 0, newBuf, 0, accLength);
                        System.Buffers.ArrayPool<byte>.Shared.Return(accBuffer);
                        accBuffer = newBuf;
                    }
                    Buffer.BlockCopy(received, 0, accBuffer, accLength, received.Length);
                    accLength += received.Length;

                    // Extract and process all complete packets from the buffer
                    while (accLength > 0)
                    {
                        var packetData = framer.TryFrame(new ReadOnlySpan<byte>(accBuffer, 0, accLength), out int consumed);
                        if (packetData == null)
                            break; // Not enough data yet, wait for more

                        // Advance the buffer past the consumed bytes
                        if (consumed >= accLength)
                        {
                            accLength = 0;
                        }
                        else
                        {
                            Buffer.BlockCopy(accBuffer, consumed, accBuffer, 0, accLength - consumed);
                            accLength -= consumed;
                        }

                        var packet = codec.Decoder.Decode<AltruistPacket>(packetData);
                        if (!await ProcessPacketAsync(codec, packet, packetData, @event, clientId))
                            return;
                    }
                }
            }
            finally
            {
                System.Buffers.ArrayPool<byte>.Shared.Return(accBuffer);
            }
        }

        /// <summary>
        /// Disconnects a client from game code: when an engine is registered the disconnect is scheduled as an engine task
        /// (so it does not race the tick), otherwise it runs immediately like <see cref="DisconnectAsync(string)"/>.
        /// </summary>
        /// <param name="clientId">Connection id.</param>
        public async Task DisconnectEngineAwareAsync(string clientId)
        {
            if (_engine != null)
            {
                _engine.SendTask(new TaskIdentifier("Disconnect_" + clientId), () => DisconnectAsync(clientId));
            }
            else
            {
                await DisconnectAsync(clientId);
            }
        }

        private async Task CloseConnection(string clientId)
        {
            var connection = await GetConnectionAsync(clientId);

            if (connection != null)
            {
                await connection.CloseOutputAsync();
                await connection.CloseAsync();
            }
        }

        /// <summary>
        /// Closes the client's connection immediately and runs the disconnect path (<c>OnDisconnectedAsync</c> of the portals
        /// on the connection's route, per-connection state cleanup, store removal) exactly once: the read loop ending
        /// because of this close does not run it again. Returns when the path has completed. A client this manager does not
        /// own a read loop for gets the path with every portal. To close gracefully after pending sends, use the outbound
        /// sender's <c>CloseAfterFlush</c>; from engine/tick code prefer <see cref="DisconnectEngineAwareAsync"/>.
        /// </summary>
        /// <param name="clientId">Connection id.</param>
        public Task DisconnectAsync(string clientId) =>
            _sessions.TryGetValue(clientId, out var session)
                ? DisconnectSessionAsync(clientId, session, null)
                : RunDisconnectPathAsync(clientId, PortalGateRegistry<IPortal>.GetAllHandlers(), null);

        private async Task DisconnectSessionAsync(string clientId, ConnectionSession session, Exception? failureException)
        {
            if (!session.TryBegin())
            {
                await session.Completion;
                return;
            }
            try
            {
                await RunDisconnectPathAsync(clientId, session.Portals, failureException);
            }
            finally
            {
                _sessions.TryRemove(new KeyValuePair<string, ConnectionSession>(clientId, session));
                session.Complete();
            }
        }

        private async Task RunDisconnectPathAsync(string clientId, IReadOnlyList<IPortal> portals, Exception? failureException)
        {
            await CloseConnection(clientId);

            foreach (var portal in portals)
            {
                try
                {
                    if (portal is OnDisconnectedAsync onDisconnectedAsync)
                    {
                        await onDisconnectedAsync.OnDisconnectedAsync(clientId, failureException);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "OnDisconnectedAsync handler threw for client {ClientId}.", clientId);
                }
            }

            // Per-connection state of the transport: the outbound queue and interceptor state (rate limits).
            _outbound?.Forget(clientId);
            foreach (var interceptor in _interceptors)
            {
                if (interceptor is IConnectionStateInterceptor stateful)
                {
                    try
                    {
                        stateful.Forget(clientId);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Interceptor {Interceptor} failed to forget client {ClientId}.", interceptor.GetType().Name, clientId);
                    }
                }
            }

            await _socketManager.RemoveConnectionAsync(clientId);
            await _socketManager.Cleanup();
        }

        // One accepted connection: the portals on its route and whether its disconnect path has started.
        private sealed class ConnectionSession
        {
            private int _started;
            private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);

            public ConnectionSession(IReadOnlyList<IPortal> portals) => Portals = portals;

            public IReadOnlyList<IPortal> Portals { get; }
            public Task Completion => _completion.Task;
            public bool TryBegin() => Interlocked.Exchange(ref _started, 1) == 0;
            public void Complete() => _completion.TrySetResult();
        }

        /// <summary>Removes the connection from the store without closing it or running disconnect hooks (see <see cref="DisconnectAsync(string)"/>).</summary>
        /// <param name="connectionId">Connection id.</param>
        public Task RemoveConnectionAsync(string connectionId)
        {
            return _socketManager.RemoveConnectionAsync(connectionId);
        }

        /// <summary>Registers a connection (see <see cref="ISocketManager.AddConnectionAsync"/>).</summary>
        /// <param name="connectionId">Connection id.</param>
        /// <param name="socket">Connection object.</param>
        /// <param name="roomId">Optional room to join.</param>
        public Task<bool> AddConnectionAsync(string connectionId, AltruistConnection socket, string? roomId = null)
        {
            return _socketManager.AddConnectionAsync(connectionId, socket, roomId);
        }

        /// <summary>Returns the connection with the given id, or <c>null</c>.</summary>
        /// <param name="connectionId">Connection id.</param>
        public Task<AltruistConnection?> GetConnectionAsync(string connectionId)
        {
            return _socketManager.GetConnectionAsync(connectionId);
        }

        /// <summary>Returns all connection ids.</summary>
        public Task<IEnumerable<string>> GetAllConnectionIdsAsync()
        {
            return _socketManager.GetAllConnectionIdsAsync();
        }

        /// <summary>Returns all connections keyed by id.</summary>
        public virtual async Task<Dictionary<string, AltruistConnection>> GetAllConnectionsDictAsync()
        {
            return await _socketManager.GetAllConnectionsDictAsync();
        }

        /// <summary>Returns a cursor over all connections.</summary>
        public Task<ICursor<AltruistConnection>> GetAllConnectionsAsync()
        {
            return _socketManager.GetAllConnectionsAsync();
        }

        private async Task<TPacketBase> ReceiveAsync<TPacketBase>(string clientId) where TPacketBase : IPacketBase
        {
            var connections = await GetAllConnectionsDictAsync();

            if (connections.TryGetValue(clientId, out var connection))
            {
                var data = await connection.ReceiveAsync(CancellationToken.None);
                var codec = _codecResolver.ResolveForConnection(connection);
                return codec.Decoder.Decode<TPacketBase>(data);
            }

            return default!;
        }

        /// <summary>Returns the connections in a room keyed by id.</summary>
        /// <param name="roomId">Room id.</param>
        public async Task<Dictionary<string, AltruistConnection>> GetConnectionsInRoomAsync(string roomId)
        {
            return await _socketManager.GetConnectionsInRoomAsync(roomId);
        }

        /// <summary>Returns the first room with free capacity, or <c>null</c>.</summary>
        public async Task<RoomPacket?> FindAvailableRoomAsync()
        {
            return await _socketManager.FindAvailableRoomAsync();
        }

        /// <summary>Returns the room the client is in, or <c>null</c>.</summary>
        /// <param name="clientId">Connection id.</param>
        public async Task<RoomPacket?> FindRoomForClientAsync(string clientId)
        {
            return await _socketManager.FindRoomForClientAsync(clientId);
        }

        /// <summary>Creates a room (or returns the existing one with that id).</summary>
        /// <param name="roomId">Room id; <c>null</c> generates one.</param>
        public async Task<RoomPacket> CreateRoomAsync(string? roomId = null)
        {
            return await _socketManager.CreateRoomAsync(roomId);
        }

        /// <summary>Deletes a room record.</summary>
        /// <param name="roomName">Room id.</param>
        public Task DeleteRoomAsync(string roomName)
        {
            return _socketManager.DeleteRoomAsync(roomName);
        }

        /// <summary>Returns a room by id, or <c>null</c>.</summary>
        /// <param name="roomId">Room id.</param>
        public Task<RoomPacket?> GetRoomAsync(string roomId)
        {
            return _socketManager.GetRoomAsync(roomId);
        }

        /// <summary>Returns all rooms keyed by id.</summary>
        public Task<Dictionary<string, RoomPacket>> GetAllRoomsAsync()
        {
            return _socketManager.GetAllRoomsAsync();
        }

        /// <summary>Moves a connection into a room (leaving its previous one).</summary>
        /// <param name="connectionId">Connection id.</param>
        /// <param name="roomId">Target room id.</param>
        public Task<RoomPacket?> JoinRoomAsync(string connectionId, string roomId)
        {
            return _socketManager.JoinRoomAsync(connectionId, roomId);
        }

        /// <summary>Persists a modified room.</summary>
        /// <param name="room">Room to save.</param>
        public async Task SaveRoomAsync(RoomPacket room)
        {
            await _socketManager.SaveRoomAsync(room);
        }

        /// <summary>No-op hook (store cleanup runs through <see cref="ISocketManager.Cleanup"/> on each disconnect).</summary>
        public virtual Task Cleanup()
        {
            return Task.CompletedTask;
        }

        /// <summary>Returns whether a connection with this id is registered.</summary>
        /// <param name="connectionId">Connection id.</param>
        public Task<bool> IsConnectionExistsAsync(string connectionId)
        {
            return _socketManager.IsConnectionExistsAsync(connectionId);
        }
    }
}
