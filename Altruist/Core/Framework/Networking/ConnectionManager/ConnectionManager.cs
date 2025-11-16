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
    public class ConnectionManager : IConnectionManager
    {
        private readonly ICodec _codec;
        private readonly List<IInterceptor> _interceptors = new();
        private readonly ISocketManager _socketManager;
        private readonly IEngineCore? _engine;
        private readonly ILogger _logger;

        public ConnectionManager(ISocketManager socketManager, ICodec codec,
         ILoggerFactory loggerFactory, IEngineCore? engineCore = null)
        {
            _socketManager = socketManager;
            _codec = codec;
            _engine = engineCore;
            _logger = loggerFactory.CreateLogger(GetType());
        }

        public void AddInterceptor(IInterceptor interceptor) => _interceptors.Add(interceptor);

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

                IPacket? message = _codec.Decoder.Decode<IPacket>(data, parameterType);

                var interceptorTasks = _interceptors
                    .Select(interceptor => interceptor.Intercept(context, message));
                var interceptorExecution = Task.WhenAll(interceptorTasks);

                Task? handlerTask = data != null
                    ? (Task?)@delegate.DynamicInvoke(message, clientId)
                    : null;

                if (handlerTask != null)
                {
                    await handlerTask.ConfigureAwait(false);
                }

                await interceptorExecution.ConfigureAwait(false);
            }
            else
            {
                _logger.LogWarning("No handler found for event: {Event}", packet.Event);
            }

            return true;
        }

        public async Task HandleConnection(AltruistConnection connection, string @event, string clientId)
        {
            await _socketManager.AddConnectionAsync(clientId, connection).ConfigureAwait(false);

            var portals = PortalGateRegistry<IPortal>.GetAllHandlers();
            foreach (var portal in portals)
            {
                try
                {
                    await portal.OnConnectedAsync(clientId).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "OnConnectedAsync handler threw for client {ClientId}.", clientId);
                }
            }

            Exception? failureException = null;

            try
            {
                while (true)
                {
                    var packetData = await connection.ReceiveAsync(CancellationToken.None).ConfigureAwait(false);
                    if (packetData.Length == 0)
                    {
                        break;
                    }

                    var packet = _codec.Decoder.Decode<AltruistPacket>(packetData);
                    if (!await ProcessPacket(packet, packetData, @event, clientId).ConfigureAwait(false))
                        break;
                }
            }
            catch (Exception ex)
            {
                failureException = ex;
                _logger.LogError(ex, "Error handling connection for client {ClientId}.", clientId);
            }
            finally
            {
                // Notify all portals about disconnection
                foreach (var portal in portals)
                {
                    try
                    {
                        if (_engine != null)
                        {
                            _engine.SendTask(new TaskIdentifier("Disconnect_" + clientId + "_" + portal.GetType().FullName), () => portal.OnDisconnectedAsync(clientId, failureException));
                        }
                        else
                        {
                            await portal.OnDisconnectedAsync(clientId, failureException);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "OnDisconnectedAsync handler threw for client {ClientId}.", clientId);
                    }
                }

                await _socketManager.RemoveConnectionAsync(clientId).ConfigureAwait(false);
                await _socketManager.Cleanup().ConfigureAwait(false);
            }
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
            return await _socketManager.GetAllConnectionsDictAsync().ConfigureAwait(false);
        }

        public Task<ICursor<AltruistConnection>> GetAllConnectionsAsync()
        {
            return _socketManager.GetAllConnectionsAsync();
        }

        private async Task<TPacketBase> ReceiveAsync<TPacketBase>(string clientId) where TPacketBase : IPacketBase
        {
            var connections = await GetAllConnectionsDictAsync().ConfigureAwait(false);

            if (connections.TryGetValue(clientId, out var connection))
            {
                var data = await connection.ReceiveAsync(CancellationToken.None).ConfigureAwait(false);
                return _codec.Decoder.Decode<TPacketBase>(data);
            }

            return default!;
        }

        public async Task<Dictionary<string, AltruistConnection>> GetConnectionsInRoomAsync(string roomId)
        {
            return await _socketManager.GetConnectionsInRoomAsync(roomId).ConfigureAwait(false);
        }

        public async Task<RoomPacket> FindAvailableRoomAsync()
        {
            return await _socketManager.FindAvailableRoomAsync().ConfigureAwait(false);
        }

        public async Task<RoomPacket?> FindRoomForClientAsync(string clientId)
        {
            return await _socketManager.FindRoomForClientAsync(clientId).ConfigureAwait(false);
        }

        public async Task<RoomPacket> CreateRoomAsync()
        {
            return await _socketManager.CreateRoomAsync().ConfigureAwait(false);
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

        public Task<RoomPacket?> AddClientToRoomAsync(string connectionId, string roomId)
        {
            return _socketManager.AddClientToRoomAsync(connectionId, roomId);
        }

        public async Task SaveRoomAsync(RoomPacket room)
        {
            await _socketManager.SaveRoomAsync(room).ConfigureAwait(false);
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
