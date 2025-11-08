
using Microsoft.Extensions.Logging;

namespace Altruist;

[Service(typeof(IConnectionManager))]
public class ConnectionManager : IConnectionManager
{
    private readonly ICodec _codec;
    private readonly List<IInterceptor> _interceptors = new();
    private readonly ISocketManager _socketManager;

    private readonly ILogger _logger;

    private event Func<string, Exception?, Task>? _onDisconnectedAsync;

    public ConnectionManager(ISocketManager socketManager, ICodec codec, ILoggerFactory loggerFactory)
    {
        _socketManager = socketManager;
        _codec = codec;
        _logger = loggerFactory.CreateLogger(GetType());
    }

    event Func<string, Exception?, Task> IConnectionManager.OnDisconnectedAsync
    {
        add { _onDisconnectedAsync += value; }
        remove { _onDisconnectedAsync -= value; }
    }

    private async Task RaiseOnDisconnectedAsync(string clientId, Exception? exception)
    {
        var handlers = _onDisconnectedAsync;
        if (handlers is null) return;

        // Snapshot & invoke each handler; don't let one failure cancel the rest
        var calls = handlers.GetInvocationList()
            .Cast<Func<string, Exception?, Task>>()
            .Select(async h =>
            {
                try { await h(clientId, exception).ConfigureAwait(false); }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "OnDisconnectedAsync handler threw for client {ClientId}.", clientId);
                }
            });

        await Task.WhenAll(calls).ConfigureAwait(false);
    }
    public void AddInterceptor(IInterceptor interceptor) => _interceptors.Add(interceptor);

    public async Task<bool> ProcessPacket(AltruistPacket packet, byte[] bytes, string @event, string clientId)
    {
        if (string.IsNullOrEmpty(packet.Event)) return false;

        if (EventHandlerRegistry<IPortal>.TryGetHandler(packet.Event, out var @delegate))
        {
            var data = bytes;
            var context = new InterceptContext(@event);
            var handlerMethod = @delegate.Method;
            var parameterType = handlerMethod.GetParameters()[0].ParameterType;

            IPacket? message = _codec.Decoder.Decode<IPacket>(data, parameterType);

            var interceptorTasks = _interceptors.Select(interceptor => interceptor.Intercept(context, message));
            var interceptorExecution = Task.WhenAll(interceptorTasks);

            Task? handlerTask = data != null ? (Task?)@delegate.DynamicInvoke(message, clientId) : null;

            if (handlerTask != null)
            {
                await handlerTask;
            }

            await interceptorExecution;
        }
        else
        {
            _logger.LogWarning($"No handler found for event: {packet.Event}");
        }

        return true;
    }

    public async Task HandleConnection(Connection connection, string @event, string clientId)
    {
        await _socketManager.AddConnectionAsync(clientId, connection);

        try
        {
            while (true)
            {
                var packetData = await connection.ReceiveAsync(CancellationToken.None);
                if (packetData.Length == 0)
                {
                    break;
                }

                var packet = _codec.Decoder.Decode<AltruistPacket>(packetData);
                if (!await ProcessPacket(packet, packetData, @event, clientId)) break;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError($"Error handling connection: {ex}");
            await RaiseOnDisconnectedAsync(clientId, ex).ConfigureAwait(false);
        }
        finally
        {
            await RaiseOnDisconnectedAsync(clientId, null).ConfigureAwait(false);
            await _socketManager.RemoveConnectionAsync(clientId);
            await _socketManager.Cleanup();
        }
    }

    public Task RemoveConnectionAsync(string connectionId)
    {
        return _socketManager.RemoveConnectionAsync(connectionId);
    }

    public Task<bool> AddConnectionAsync(string connectionId, Connection socket, string? roomId = null)
    {
        return _socketManager.AddConnectionAsync(connectionId, socket, roomId);
    }

    public Task<Connection?> GetConnectionAsync(string connectionId)
    {
        return _socketManager.GetConnectionAsync(connectionId);
    }

    public Task<IEnumerable<string>> GetAllConnectionIdsAsync()
    {
        return _socketManager.GetAllConnectionIdsAsync();
    }

    public virtual async Task<Dictionary<string, Connection>> GetAllConnectionsDictAsync()
    {
        return await _socketManager.GetAllConnectionsDictAsync();
    }

    public Task<ICursor<Connection>> GetAllConnectionsAsync()
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

    public async Task<Dictionary<string, Connection>> GetConnectionsInRoomAsync(string roomId)
    {
        return await _socketManager.GetConnectionsInRoomAsync(roomId);
    }

    public async Task<RoomPacket> FindAvailableRoomAsync()
    {
        return await _socketManager.FindAvailableRoomAsync();
    }

    public async Task<RoomPacket?> FindRoomForClientAsync(string clientId)
    {
        return await _socketManager.FindRoomForClientAsync(clientId);
    }

    public async Task<RoomPacket> CreateRoomAsync()
    {
        return await _socketManager.CreateRoomAsync();
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