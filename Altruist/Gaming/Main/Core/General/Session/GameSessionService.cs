using System.Collections.Concurrent;
using Altruist;
using Microsoft.Extensions.Logging;

public interface IGameSessionService
{
    // Core session lifecycle
    Task Cleanup();
    Task ExitGameAsync(LeaveGamePacket message, string clientId);
    Task JoinGameAsync(JoinGamePacket message, string clientId);
    Task HandshakeAsync(HandshakePacket message, string clientId);

    // ---- Minimal Session Context API (type-only) ----
    Task SetContext<T>(string clientId, T value);
    Task<T?> GetContext<T>(string clientId);
    Task RemoveContext<T>(string clientId);
}

[Service(typeof(IGameSessionService))]
public class GameSessionService : IGameSessionService
{
    private readonly ISocketManager _socketManager;
    private readonly IAltruistRouter _router;
    private readonly ILogger _logger;

    // clientId -> list of arbitrary context objects
    private readonly ConcurrentDictionary<string, List<object>> _contexts =
        new(StringComparer.Ordinal);

    public GameSessionService(
        ISocketManager socketManager,
        IAltruistRouter router,
        ILoggerFactory loggerFactory)
    {
        _socketManager = socketManager;
        _router = router;
        _logger = loggerFactory.CreateLogger(GetType());
    }

    // -------------------------
    // Session lifecycle methods
    // -------------------------

    public async virtual Task HandshakeAsync(HandshakePacket message, string clientId)
    {
        var rooms = await _socketManager.GetAllRoomsAsync();
        var responsePacket = new HandshakePacket("server", rooms.Values.ToArray(), clientId);
        await _router.Client.SendAsync(clientId, responsePacket);
    }

    public async virtual Task ExitGameAsync(LeaveGamePacket message, string clientId)
    {
        var room = await _socketManager.FindRoomForClientAsync(clientId);
        var msg = $"Disconnected from the server";

        _ = _router.Client.SendAsync(clientId, PacketHelper.Success(msg, clientId, message.Type));

        if (room != null)
        {
            var broadcastPacket = new LeaveGamePacket("server", clientId);
            room = room.RemoveConnection(clientId);
            _ = _socketManager.SaveRoomAsync(room);
            _ = _router.Room.SendAsync(room.Id, broadcastPacket);

            if (room.Empty())
                await _socketManager.DeleteRoomAsync(room.Id);
        }

        // Clear contexts on exit (optional — comment out to keep sticky contexts)
        ClearAllContexts(clientId);
    }

    public async virtual Task JoinGameAsync(JoinGamePacket message, string clientId)
    {
        if (string.IsNullOrEmpty(message.Name))
        {
            await _router.Client.SendAsync(clientId, PacketHelper.Failed("Username is required!", clientId, message.Type));
            return;
        }

        RoomPacket? room;
        if (!string.IsNullOrEmpty(message.RoomId))
        {
            room = await _socketManager.GetRoomAsync(message.RoomId);
            if (room == null)
            {
                var joinFailedMsg = $"Join failed. No such room: {message.RoomId}";
                await _router.Client.SendAsync(clientId, PacketHelper.Failed(joinFailedMsg, clientId, message.Type));
                return;
            }
        }
        else
        {
            room = await _socketManager.FindAvailableRoomAsync();
        }

        if (room == null)
        {
            var msg = $"Join failed: No available rooms";
            await _router.Client.SendAsync(clientId, PacketHelper.Failed(msg, clientId, message.Type));
            _logger.LogWarning(msg);
        }
        else if (room.Has(clientId))
        {
            var msg = $"Join failed: {clientId} is already in the game";
            await _router.Client.SendAsync(clientId, PacketHelper.Failed(msg, clientId, message.Type));
            _logger.LogWarning(msg);
        }
        else
        {
            var msg = $"Player {message.Name} joined the room: {room.Id}.";
            _logger.LogInformation(msg);
            // Userland code can now SetContext<Account/Character/etc>(clientId, value) elsewhere.
        }
    }

    public async Task Cleanup()
    {
        try
        {
            await _socketManager.Cleanup();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error cleaning up connections.");
        }
    }

    // -------------------------
    // Minimal type-only context API
    // -------------------------

    public Task SetContext<T>(string clientId, T value)
    {
        var list = _contexts.GetOrAdd(clientId, _ => new List<object>());
        lock (list)
        {
            // ensure only one instance per type (last write wins)
            for (int i = list.Count - 1; i >= 0; --i)
            {
                if (list[i] is T) { list.RemoveAt(i); break; }
            }
            list.Add(value!);
        }
        return Task.CompletedTask;
    }

    public Task<T?> GetContext<T>(string clientId)
    {
        if (_contexts.TryGetValue(clientId, out var list))
        {
            lock (list)
            {
                // return the most recently added of type T (if any)
                for (int i = list.Count - 1; i >= 0; --i)
                {
                    if (list[i] is T t) return Task.FromResult<T?>(t);
                }
            }
        }
        return Task.FromResult<T?>(default);
    }

    public Task RemoveContext<T>(string clientId)
    {
        if (_contexts.TryGetValue(clientId, out var list))
        {
            lock (list)
            {
                for (int i = list.Count - 1; i >= 0; --i)
                {
                    if (list[i] is T) { list.RemoveAt(i); break; }
                }
                if (list.Count == 0)
                    _contexts.TryRemove(clientId, out _);
            }
        }
        return Task.CompletedTask;
    }

    private void ClearAllContexts(string clientId)
    {
        _contexts.TryRemove(clientId, out _);
    }
}
