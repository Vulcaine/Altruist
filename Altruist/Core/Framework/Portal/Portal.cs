using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Altruist;

public class PortalContext(
    IAltruistContext altruistContext,
    IAltruistRouter router, ICodec codec, IConnectionStore connectionStore, ICacheProvider cache, IServiceProvider serviceProvider)
    : AbstractSocketPortalContext(altruistContext, router, codec, connectionStore, cache, serviceProvider)
{
    public override IAltruistRouter Router { get; protected set; } = router;
    public override ICodec Codec { get; protected set; } = codec;
    public override IAltruistContext AltruistContext { get; protected set; } = altruistContext;
    public override IServiceProvider ServiceProvider { get; protected set; } = serviceProvider;

    public override ICacheProvider Cache { get; protected set; } = cache;

    public override void Initialize() { }
}

public abstract class Portal : IPortal, IConnectionStore
{
    private readonly IPortalContext _context;
    protected readonly ILogger Logger;

    protected IAltruistRouter Router => _context.Router;
    private ICodec _codec => _context.Codec;

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

    public async Task HandleConnection(Connection connection, string @event, string clientId)
    {
        await AddConnectionAsync(clientId, connection);

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
            await RemoveConnectionAsync(clientId);
            await Cleanup();
        }
    }


    public Task RemoveConnectionAsync(string connectionId)
    {
        return _context.RemoveConnectionAsync(connectionId);
    }

    public Task<bool> AddConnectionAsync(string connectionId, Connection socket, string? roomId = null)
    {
        return _context.AddConnectionAsync(connectionId, socket, roomId);
    }

    public Task<Connection?> GetConnectionAsync(string connectionId)
    {
        return _context.GetConnectionAsync(connectionId);
    }

    public Task<IEnumerable<string>> GetAllConnectionIdsAsync()
    {
        return _context.GetAllConnectionIdsAsync();
    }

    public Task<Dictionary<string, Connection>> GetAllConnectionsAsync()
    {
        return _context.GetAllConnectionsAsync();
    }

    private async Task<TPacketBase> ReceiveAsync<TPacketBase>(string clientId) where TPacketBase : IPacketBase
    {
        var connections = await GetAllConnectionsAsync();
        if (connections.TryGetValue(clientId, out var connection))
        {
            var data = await connection.ReceiveAsync(CancellationToken.None);
            return _codec.Decoder.Decode<TPacketBase>(data);
        }

        return default!;
    }


    public async Task<Dictionary<string, Connection>> GetConnectionsInRoomAsync(string roomId)
    {
        return await _context.GetConnectionsInRoomAsync(roomId);
    }

    public async Task<RoomPacket> FindAvailableRoomAsync()
    {
        return await _context.FindAvailableRoomAsync();
    }

    public async Task<RoomPacket?> FindRoomForClientAsync(string clientId)
    {
        return await _context.FindRoomForClientAsync(clientId);
    }

    public async Task<RoomPacket> CreateRoomAsync()
    {
        return await _context.CreateRoomAsync();
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

    public Task<RoomPacket?> AddClientToRoomAsync(string connectionId, string roomId)
    {
        return _context.AddClientToRoomAsync(connectionId, roomId);
    }

    public async Task SaveRoomAsync(RoomPacket room)
    {
        await _context.SaveRoomAsync(room);
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

    public Task<bool> IsConnectionExistsAsync(string connectionId)
    {
        return _context.IsConnectionExistsAsync(connectionId);
    }
}
