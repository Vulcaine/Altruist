using System.Collections;
using Microsoft.Extensions.Logging;
using Redis.OM;
using Redis.OM.Searching;
using StackExchange.Redis;

namespace Altruist.Redis;

public class RedisCacheCursor<T> : ICacheCursor<T>, IEnumerable<T> where T : notnull
{
    private int BatchSize { get; }
    private int CurrentIndex { get; set; }
    private List<T> CurrentBatch { get; }

    private readonly RedisConnectionProvider _provider;

    public List<T> Items => CurrentBatch;
    public bool HasNext => CurrentBatch.Count == BatchSize;

    public RedisCacheCursor(RedisConnectionProvider provider, int batchSize)
    {
        _provider = provider;
        BatchSize = batchSize;
        CurrentIndex = 0;
        CurrentBatch = new List<T>();
    }

    public async Task<bool> NextBatch()
    {
        CurrentBatch.Clear();
        var repo = _provider.RedisCollection<T>();
        var entities = await repo.Skip(CurrentIndex).Take(BatchSize).ToListAsync();

        if (entities.Count == 0)
            return false;

        CurrentBatch.AddRange(entities);
        CurrentIndex += entities.Count;
        return true;
    }

    private IEnumerable<T> FetchAllBatches()
    {
        do
        {
            foreach (var item in CurrentBatch)
            {
                yield return item;
            }
        } while (NextBatch().GetAwaiter().GetResult());
    }

    public IEnumerator<T> GetEnumerator()
    {
        return FetchAllBatches().GetEnumerator();
    }

    IEnumerator IEnumerable.GetEnumerator()
    {
        return GetEnumerator();
    }
}


public interface IAltruistRedisProvider : IExternalCache
{

}

public interface IAltruistRedisConnectionProvider : IConnectionStore
{
    Task<bool> CreateIndexAsync(Type type);
    IRedisCollection<TNonNullType> RedisCollection<TNonNullType>() where TNonNullType : notnull;
    RedisResult ScriptEvaluate(string script, RedisKey[]? keys = null, RedisValue[]? values = null, CommandFlags flags = CommandFlags.None);
}


public sealed class RedisCache : IAltruistRedisProvider
{
    private readonly RedisConnectionProvider _provider;
    private readonly IDatabase _redis;

    public RedisCache(
        RedisConnectionProvider provider,
        IConnectionMultiplexer mux)
    {
        _redis = mux.GetDatabase();
        _provider = provider;
    }

    public async Task<T?> GetAsync<T>(string key) where T : notnull
    {
        var repo = _provider.RedisCollection<T>();
        var entity = await repo.FindByIdAsync(key);
        return entity;
    }

    public Task<ICacheCursor<T>> GetAllAsync<T>(int batchSize = 100) where T : notnull
    {
        var cursor = new RedisCacheCursor<T>(_provider, batchSize);
        return Task.FromResult(cursor as ICacheCursor<T>);
    }

    public Task<object?> GetAsync(string key, Type type)
    {
        throw new NotSupportedException("GetAsync(string key, Type type) is not supported when using Redis OM.");
    }

    public async Task SaveAsync<T>(string key, T entity) where T : notnull
    {
        var repo = _provider.RedisCollection<T>();
        await repo.InsertAsync(entity);
        await repo.SaveAsync();
    }

    public Task SaveAsync(string key, object entity, Type type)
    {
        throw new NotSupportedException("SaveAsync(string key, object entity, Type type) is not supported when using Redis OM.");
    }

    public async Task SaveBatchAsync<T>(Dictionary<string, T> entities) where T : notnull
    {
        var repo = _provider.RedisCollection<T>();
        foreach (var (key, entity) in entities)
        {
            await repo.InsertAsync(entity);
        }
        await repo.SaveAsync();
    }

    public Task SaveBatchAsync(Dictionary<string, object> entities, Type type)
    {
        throw new NotSupportedException("SaveBatchAsync(Dictionary<string, object> entities, Type type) is not supported when using Redis OM.");
    }

    public async Task<T?> RemoveAsync<T>(string key) where T : notnull
    {
        var repo = _provider.RedisCollection<T>();

        var element = await repo.FindByIdAsync(key);
        if (element != null)
        {
            await repo.DeleteAsync(element);
        }

        return element;
    }

    public Task RemoveAsync(string key, Type type)
    {
        throw new NotSupportedException("RemoveAsync(string key, Type type) is not supported when using Redis OM.");
    }

    // public async Task<ICacheCursor<string>> GetBatchKeysAsync(string baseKey, int skip, int take)
    // {
    //     var repo = _provider.RedisCollection<object>();
    //     var keys = await repo.KeysAsync(baseKey);
    //     return keys.Skip(skip).Take(take).ToList();
    // }

    public async Task ClearAsync<T>() where T : notnull
    {
        var repo = _provider.RedisCollection<T>();
        var allItems = await repo.ToListAsync();
        await repo.DeleteAsync(allItems);
    }

    public async Task<ICacheCursor<T>> GetAllAsync<T>() where T : notnull
    {
        return await CreateCursorAsync<T>(nameof(T), 100);
    }

    public Task<ICacheCursor<object>> GetAllAsync(Type type)
    {
        throw new NotSupportedException("GetAllAsync(Type type) is not supported when using Redis OM.");
    }

    public Task ClearAllAsync()
    {
        throw new NotSupportedException("ClearAllAsync() is not supported when using Redis OM.");
    }

    private Task<ICacheCursor<T>> CreateCursorAsync<T>(string baseKey, int batchSize) where T : notnull
    {
        var cursor = new RedisCacheCursor<T>(_provider, batchSize);
        return Task.FromResult(cursor as ICacheCursor<T>);
    }
}

public sealed class RedisConnectionService : AbstractConnectionStore, IAltruistRedisConnectionProvider
{
    private readonly RedisCache _cache;
    private readonly IDatabase _redis;
    private readonly RedisConnectionProvider _provider;
    private const string GlobalKey = "AltruistConnections";
    private const string RoomPrefix = "room:";

    public RedisConnectionService(RedisConnectionProvider provider, IConnectionMultiplexer redis,
        IMemoryCache memoryCache,
        RedisCache cache, ILoggerFactory loggerFactory) : base(memoryCache, loggerFactory)
    {
        _redis = redis.GetDatabase();
        _provider = provider;
        _cache = cache;
    }

    public override async Task<bool> AddConnection(string connectionId, IConnection socket, string? roomId = null)
    {
        if (!await base.AddConnection(connectionId, socket))
        {
            return false;
        }

        await _redis.SetAddAsync(GlobalKey, connectionId);

        if (!string.IsNullOrEmpty(roomId))
        {
            var roomKey = $"{RoomPrefix}{roomId}";
            var existingRoom = await _cache.GetAsync<RoomPacket>(roomKey);
            if (existingRoom != null)
            {
                existingRoom.ConnectionIds.Add(connectionId);
                await _cache.SaveAsync(roomKey, existingRoom);
                return true;
            }
            else
            {
                return false;
            }
        }

        return true;
    }

    public override async Task RemoveConnection(string connectionId)
    {
        await base.RemoveConnection(connectionId);
        await _redis.SetRemoveAsync(GlobalKey, connectionId);

        var server = _redis.Multiplexer.GetServer(_redis.Multiplexer.GetEndPoints()[0]);
        var roomKeys = server.Keys(pattern: $"{RoomPrefix}*");

        foreach (var roomKey in roomKeys)
        {
            var room = await _cache.GetAsync<RoomPacket>(roomKey!);
            if (room == null)
                continue;

            if (room.ConnectionIds.Remove(connectionId) || room.Empty())
            {
                if (room.Empty())
                    await _cache.RemoveAsync<RoomPacket>(roomKey!);
                else
                    await _cache.SaveAsync(roomKey!, room);
            }
        }
    }

    public override async Task<RoomPacket?> GetRoomAsync(string roomId)
    {
        return await _cache.GetAsync<RoomPacket>(roomId);
    }

    public override async Task<Dictionary<string, RoomPacket>> GetAllRoomsAsync()
    {
        var rooms = new Dictionary<string, RoomPacket>();
        var server = _redis.Multiplexer.GetServer(_redis.Multiplexer.GetEndPoints()[0]);
        var roomKeys = server.Keys(pattern: $"{RoomPrefix}*");

        foreach (var key in roomKeys)
        {
            var room = await _cache.GetAsync<RoomPacket>(key!);
            if (!EqualityComparer<RoomPacket>.Default.Equals(room, default))
            {
                rooms[room.Id] = room;
            }
        }

        return rooms;
    }

    public override async Task<RoomPacket> FindAvailableRoomAsync()
    {
        var server = _redis.Multiplexer.GetServer(_redis.Multiplexer.GetEndPoints()[0]);
        var roomKeys = server.Keys(pattern: $"{RoomPrefix}*");

        foreach (var roomKey in roomKeys)
        {
            var room = await _cache.GetAsync<RoomPacket>(roomKey!);
            if (room != null && !room.Full())
            {
                return room;
            }
        }

        return await CreateRoom();
    }

    public override async Task<RoomPacket> CreateRoom()
    {
        var newRoom = await base.CreateRoom();
        await _cache.SaveAsync($"{RoomPrefix}{newRoom.Id}", newRoom);
        return newRoom;
    }

    public override async Task SaveRoom(RoomPacket roomPacket)
    {
        await _cache.SaveAsync($"{RoomPrefix}{roomPacket.Id}", roomPacket);
    }

    public override async Task DeleteRoom(string roomId)
    {
        await _cache.RemoveAsync<RoomPacket>(roomId);
    }

    public IRedisCollection<TNonNullClass> RedisCollection<TNonNullClass>() where TNonNullClass : notnull
    {
        return _provider.RedisCollection<TNonNullClass>();
    }

    public RedisResult ScriptEvaluate(string script, RedisKey[]? keys = null, RedisValue[]? values = null, CommandFlags flags = CommandFlags.None)
    {
        return _redis.ScriptEvaluate(script, keys, values, flags);
    }

    public async Task<bool> CreateIndexAsync(Type type)
    {
        return await _provider.Connection.CreateIndexAsync(type);
    }
}
