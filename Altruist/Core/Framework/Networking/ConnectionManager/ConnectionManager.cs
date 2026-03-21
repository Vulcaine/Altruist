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

namespace Altruist
{

    struct DisconnectToken
    {

    }

    [Service(typeof(IConnectionManager))]
    [ConditionalOnConfig("altruist:server:transport")]
    public class ConnectionManager : IConnectionManager
    {
        private readonly ICodec _codec;
        private readonly List<IInterceptor> _interceptors = new();
        private readonly ISocketManager _socketManager;
        private readonly IEngineCore? _engine;
        private readonly ILogger _logger;

        private readonly int _idleTimeout;

        public ConnectionManager(
            ISocketManager socketManager,
            ICodec codec,
            ILoggerFactory loggerFactory, IEngineCore? engineCore = null,
            [AppConfigValue("altruist:server:transport:timeout", "10")] int timeout = 10
         )
        {
            _socketManager = socketManager;
            _codec = codec;
            _engine = engineCore;
            _logger = loggerFactory.CreateLogger(GetType());
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
                if (conn.Route == portal.Route)
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

            if (PortalGateRegistry<IPortal>.TryGetHandler(packet.Event, out var @delegate))
            {
                var data = bytes;
                var context = new InterceptContext(@event);
                var handlerMethod = @delegate.Method;
                var parameterType = handlerMethod.GetParameters()[0].ParameterType;

                IPacket? message;
                try
                {
                    if (data.Length > 0)
                    {
                        message = _codec.Decoder.Decode<IPacket>(data, parameterType);
                        _logger.LogInformation("Decoded {Len} bytes as {Type} OK", data.Length, parameterType.Name);
                    }
                    else
                    {
                        message = (IPacket?)Activator.CreateInstance(parameterType);
                        _logger.LogInformation("Empty payload, created default {Type}", parameterType.Name);
                    }
                }
                catch (Exception decodeEx)
                {
                    _logger.LogWarning("Failed to decode {Len} bytes as {Type}: {Error}", data.Length, parameterType.Name, decodeEx.Message);
                    message = (IPacket?)Activator.CreateInstance(parameterType);
                }

                var interceptorTasks = _interceptors
                    .Select(interceptor => interceptor.Intercept(context, message));
                var interceptorExecution = Task.WhenAll(interceptorTasks);

                PacketContext.Set(data);
                try
                {
                    Task? handlerTask = data != null
                        ? (Task?)@delegate.DynamicInvoke(message, clientId)
                        : null;

                    if (handlerTask != null)
                    {
                        await handlerTask;
                    }

                    await interceptorExecution;
                }
                finally
                {
                    PacketContext.Clear();
                }
            }
            else
            {
                _logger.LogWarning("No handler found for event: {Event}", packet.Event);
            }

            return true;
        }

        public async Task HandleConnection(AltruistConnection connection, string @event, string clientId)
        {
            await _socketManager.AddConnectionAsync(clientId, connection, StoreConstants.WaitingRoomId);

            var portals = PortalGateRegistry<IPortal>.GetAllHandlers();
            foreach (var portal in portals)
            {
                if (portal.Route != connection.Route)
                {
                    continue;
                }

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
                if (_codec is IFramedCodec framedCodec)
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
            while (true)
            {
                byte[] packetData;

                using var cts = new CancellationTokenSource(idleTimeout);
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

                // Frame format: [1-byte eventLen][eventName UTF8][payload]
                // If first byte looks like a valid event length (1-127), use event-prefixed framing.
                // Otherwise fall back to AltruistPacket decode for backward compat.
                AltruistPacket packet;
                byte[] payloadBytes;

                if (packetData.Length > 2 && packetData[0] > 0 && packetData[0] < 128)
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
                        packet = _codec.Decoder.Decode<AltruistPacket>(packetData);
                        payloadBytes = packetData;
                    }
                }
                else
                {
                    packet = _codec.Decoder.Decode<AltruistPacket>(packetData);
                    payloadBytes = packetData;
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
            byte[] accumulator = Array.Empty<byte>();

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

                // Append received data to accumulator
                if (accumulator.Length == 0)
                {
                    accumulator = received;
                }
                else
                {
                    var combined = new byte[accumulator.Length + received.Length];
                    accumulator.CopyTo(combined, 0);
                    received.CopyTo(combined, accumulator.Length);
                    accumulator = combined;
                }

                // Extract and process all complete packets from the buffer
                while (accumulator.Length > 0)
                {
                    var packetData = framer.TryFrame(accumulator, out int consumed);
                    if (packetData == null)
                        break; // Not enough data yet, wait for more

                    // Advance the buffer past the consumed bytes
                    if (consumed >= accumulator.Length)
                        accumulator = Array.Empty<byte>();
                    else
                        accumulator = accumulator[consumed..];

                    var packet = _codec.Decoder.Decode<AltruistPacket>(packetData);
                    if (!await ProcessPacket(packet, packetData, @event, clientId))
                        return;
                }
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
                return _codec.Decoder.Decode<TPacketBase>(data);
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
