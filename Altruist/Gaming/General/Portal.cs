using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Altruist;

public class PortalContext(
    IAltruistContext altruistContext,
    IAltruistRouter router, IMessageCodec codec, IConnectionStore connectionStore, ICache cache, IServiceProvider serviceProvider)
    : AbstractSocketPortalContext(altruistContext, router, codec, connectionStore, cache, serviceProvider)
{
    public override IAltruistRouter Router { get; protected set; } = router;
    public override IMessageCodec Codec { get; protected set; } = codec;
    public override IAltruistContext AltruistContext { get; protected set; } = altruistContext;
    public override IServiceProvider ServiceProvider { get; protected set; } = serviceProvider;

    public override ICache Cache { get; protected set; } = cache;

    public override void Initialize() { }

    public override IPlayerService<TPlayerEntity> GetPlayerService<TPlayerEntity>()
    {
        return ServiceProvider.GetRequiredService<IPlayerService<TPlayerEntity>>();
    }
}

public abstract class Portal : IPortal, IConnectionStore
{
    private readonly IPortalContext _context;
    protected readonly ILogger Logger;

    protected IAltruistRouter Router => _context.Router;
    private IMessageCodec _codec => _context.Codec;

    private readonly List<IInterceptor> _interceptors = new();

    public Portal(IPortalContext context, ILoggerFactory loggerFactory)
    {
        _context = context;
        Logger = loggerFactory.CreateLogger<Portal>();
    }

    public void AddInterceptor(IInterceptor interceptor) => _interceptors.Add(interceptor);

    public virtual async Task OnDisconnectedAsync(string clientId, Exception? exception = null)
    {
        await Cleanup();
    }

    public virtual Task OnConnectedAsync(string clientId)
    {
        return Task.CompletedTask;
    }

    protected async Task<bool> ProcessPacket(AltruistPacket packet, byte[] bytes, string @event, string clientId)
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
            Logger.LogWarning($"No handler found for event: {packet.Event}");
        }

        return true;
    }

    public async Task HandleConnection(IConnection connection, string @event, string clientId)
    {
        await AddConnection(clientId, connection);

        try
        {
            while (true)
            {
                var packetData = await connection.ReceiveAsync(CancellationToken.None);
                if (packetData.Length == 0)
                {
                    break;
                }
                ;

                var packet = _codec.Decoder.Decode<AltruistPacket>(packetData);
                if (!await ProcessPacket(packet, packetData, @event, clientId)) break;
            }
        }
        catch (Exception ex)
        {
            Logger.LogError($"Error handling connection: {ex}");
            await OnDisconnectedAsync(clientId, ex);
        }
        finally
        {
            await OnDisconnectedAsync(clientId);
            await RemoveConnection(clientId);
            await Cleanup();
        }
    }


    public Task RemoveConnection(string connectionId)
    {
        return _context.RemoveConnection(connectionId);
    }

    public Task<bool> AddConnection(string connectionId, IConnection socket, string? roomId = null)
    {
        return _context.AddConnection(connectionId, socket, roomId);
    }

    public Task<IConnection?> GetConnection(string connectionId)
    {
        return _context.GetConnection(connectionId);
    }

    public Task<IEnumerable<string>> GetAllConnectionIds()
    {
        return _context.GetAllConnectionIds();
    }

    public Task<Dictionary<string, IConnection>> GetAllConnections()
    {
        return _context.GetAllConnections();
    }

    private async Task<TPacketBase> ReceiveAsync<TPacketBase>(string clientId) where TPacketBase : IPacketBase
    {
        var connections = await GetAllConnections();
        if (connections.TryGetValue(clientId, out var connection))
        {
            var data = await connection.ReceiveAsync(CancellationToken.None);
            return _codec.Decoder.Decode<TPacketBase>(data);
        }

        return default!;
    }


    public async Task<Dictionary<string, IConnection>> GetConnectionsInRoom(string roomId)
    {
        return await _context.GetConnectionsInRoom(roomId);
    }

    public async Task<RoomPacket> FindAvailableRoomAsync()
    {
        return await _context.FindAvailableRoomAsync();
    }

    public async Task<RoomPacket?> FindRoomForClientAsync(string clientId)
    {
        return await _context.FindRoomForClientAsync(clientId);
    }

    public async Task<RoomPacket> CreateRoom()
    {
        return await _context.CreateRoom();
    }

    public Task DeleteRoomAsync(string roomName)
    {
        return _context.DeleteRoomAsync(roomName);
    }

    public Task<RoomPacket?> GetRoomAsync(string roomId)
    {
        return _context.GetRoomAsync(roomId);
    }

    public Task<Dictionary<string, RoomPacket>> GetAllRoomsAsync()
    {
        return _context.GetAllRoomsAsync();
    }

    public Task<RoomPacket?> AddClientToRoom(string connectionId, string roomId)
    {
        return _context.AddClientToRoom(connectionId, roomId);
    }

    public async Task SaveRoom(RoomPacket room)
    {
        await _context.SaveRoom(room);
    }

    [Cycle(cron: CronPresets.Hourly)]
    public virtual async Task Cleanup()
    {
        try
        {
            await _context.Cleanup();
        }
        catch (Exception ex)
        {
            Logger.LogError($"Error cleaning up connections: {ex}");
        }
    }
}
