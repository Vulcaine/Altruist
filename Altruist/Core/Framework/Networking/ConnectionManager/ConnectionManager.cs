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

        private readonly int _idleTimeout;

        private static readonly System.Collections.Concurrent.ConcurrentDictionary<System.Reflection.MethodInfo, System.Reflection.ParameterInfo[]> ParameterCache = new();

        public ConnectionManager(
            ISocketManager socketManager,
            ICodecResolver codecResolver,
            ILoggerFactory loggerFactory, IEngineCore? engineCore = null,
            IDashboardNetworkRecorder? networkRecorder = null,
            [AppConfigValue("altruist:server:transport:timeout", "10")] int timeout = 10
         )
        {
            _socketManager = socketManager;
            _codecResolver = codecResolver;
            _defaultCodec = codecResolver.Resolve();
            _engine = engineCore;
            _logger = loggerFactory.CreateLogger(GetType());
            _networkRecorder = networkRecorder;
            _idleTimeout = timeout;

            Initialize();
        }

        private void Initialize()
        {
            CreateRoomAsync(StoreConstants.WaitingRoomId).GetAwaiter();
        }

        public void AddInterceptor(IInterceptor interceptor) => _interceptors.Add(interceptor);

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

        public async Task<bool> ProcessPacket(AltruistPacket packet, byte[] bytes, string @event, string clientId)
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
                var context = new InterceptContext(packet.Event, clientId, data.Length);
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
                        message = _defaultCodec.Decoder.Decode<IPacket>(data, parameterType);
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
                    _logger.LogWarning("Failed to decode {Len} bytes as {Type}: {Error}", data.Length, parameterType?.Name ?? "empty", decodeEx.Message);
                    error = decodeEx.Message;
                    // Never hand a default-constructed packet to the handler: drop the message.
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
                    return false;
                }

                // Interceptors finish before the handler runs so they can veto the packet
                // (rate limits, size caps). A single interceptor is awaited directly (no array/WhenAll).
                if (_interceptors.Count == 1 && message is not null)
                {
                    await _interceptors[0].Intercept(context, message);
                    if (context.Rejected)
                        return true;
                }
                else if (_interceptors.Count > 0 && message is not null)
                {
                    var tasks = new Task[_interceptors.Count];
                    for (int i = 0; i < _interceptors.Count; i++)
                        tasks[i] = _interceptors[i].Intercept(context, message);
                    await Task.WhenAll(tasks);
                    if (context.Rejected)
                        return true;
                }

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
                if (_interceptors.Count > 0)
                {
                    var context = new InterceptContext(packet.Event, clientId, bytes.Length);
                    var tasks = new Task[_interceptors.Count];
                    for (int i = 0; i < _interceptors.Count; i++)
                        tasks[i] = _interceptors[i].Intercept(context, null!);
                    await Task.WhenAll(tasks);
                }
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

            try
            {
                if (_defaultCodec is IFramedCodec framedCodec)
                    await RunFramedReadLoop(connection, framedCodec.Framer, @event, clientId, idleTimeout);
                else
                    await RunStandardReadLoop(connection, @event, clientId, idleTimeout);
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
                    await DisconnectAsync(clientId, portals, failureException);
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
            AltruistConnection connection, string @event, string clientId, TimeSpan idleTimeout)
        {
            // One timeout source per connection, re-armed per message (a new source plus timer per
            // message was a large share of the per-packet allocations at 60 inputs/s).
            using var cts = new CancellationTokenSource();
            bool isJsonCodec = _defaultCodec.GetType().Name.Contains("Json", StringComparison.OrdinalIgnoreCase);
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
                        packet = _defaultCodec.Decoder.Decode<AltruistPacket>(packetData);
                        payloadBytes = packetData;
                    }
                }
                else
                {
                    packet = _defaultCodec.Decoder.Decode<AltruistPacket>(packetData);

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

                if (!await ProcessPacket(packet, payloadBytes, @event, clientId))
                    break;
            }
        }

        /// <summary>
        /// Framed read loop for raw stream protocols (e.g. binary TCP).
        /// Buffers incoming bytes and uses the IPacketFramer to extract complete packets.
        /// Multiple packets per read are processed; partial packets are carried over.
        /// </summary>
        private async Task RunFramedReadLoop(
            AltruistConnection connection, IPacketFramer framer, string @event, string clientId, TimeSpan idleTimeout)
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

                        var packet = _defaultCodec.Decoder.Decode<AltruistPacket>(packetData);
                        if (!await ProcessPacket(packet, packetData, @event, clientId))
                            return;
                    }
                }
            }
            finally
            {
                System.Buffers.ArrayPool<byte>.Shared.Return(accBuffer);
            }
        }

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

        public async Task DisconnectAsync(string clientId) => await DisconnectAsync(clientId, PortalGateRegistry<IPortal>.GetAllHandlers(), null);

        private async Task DisconnectAsync(string clientId, IReadOnlyList<IPortal> portals, Exception? failureException)
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

            await _socketManager.RemoveConnectionAsync(clientId);
            await _socketManager.Cleanup();
        }

        public Task RemoveConnectionAsync(string connectionId)
        {
            return _socketManager.RemoveConnectionAsync(connectionId);
        }

        public Task<bool> AddConnectionAsync(string connectionId, AltruistConnection socket, string? roomId = null)
        {
            return _socketManager.AddConnectionAsync(connectionId, socket, roomId);
        }

        public Task<AltruistConnection?> GetConnectionAsync(string connectionId)
        {
            return _socketManager.GetConnectionAsync(connectionId);
        }

        public Task<IEnumerable<string>> GetAllConnectionIdsAsync()
        {
            return _socketManager.GetAllConnectionIdsAsync();
        }

        public virtual async Task<Dictionary<string, AltruistConnection>> GetAllConnectionsDictAsync()
        {
            return await _socketManager.GetAllConnectionsDictAsync();
        }

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

        public async Task<Dictionary<string, AltruistConnection>> GetConnectionsInRoomAsync(string roomId)
        {
            return await _socketManager.GetConnectionsInRoomAsync(roomId);
        }

        public async Task<RoomPacket?> FindAvailableRoomAsync()
        {
            return await _socketManager.FindAvailableRoomAsync();
        }

        public async Task<RoomPacket?> FindRoomForClientAsync(string clientId)
        {
            return await _socketManager.FindRoomForClientAsync(clientId);
        }

        public async Task<RoomPacket> CreateRoomAsync(string? roomId = null)
        {
            return await _socketManager.CreateRoomAsync(roomId);
        }

        public Task DeleteRoomAsync(string roomName)
        {
            return _socketManager.DeleteRoomAsync(roomName);
        }

        public Task<RoomPacket?> GetRoomAsync(string roomId)
        {
            return _socketManager.GetRoomAsync(roomId);
        }

        public Task<Dictionary<string, RoomPacket>> GetAllRoomsAsync()
        {
            return _socketManager.GetAllRoomsAsync();
        }

        public Task<RoomPacket?> JoinRoomAsync(string connectionId, string roomId)
        {
            return _socketManager.JoinRoomAsync(connectionId, roomId);
        }

        public async Task SaveRoomAsync(RoomPacket room)
        {
            await _socketManager.SaveRoomAsync(room);
        }

        public virtual Task Cleanup()
        {
            return Task.CompletedTask;
        }

        public Task<bool> IsConnectionExistsAsync(string connectionId)
        {
            return _socketManager.IsConnectionExistsAsync(connectionId);
        }
    }
}
