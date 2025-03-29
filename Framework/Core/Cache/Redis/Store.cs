using Microsoft.Extensions.Logging;
using Redis.OM;
using Redis.OM.Searching;
using StackExchange.Redis;

namespace Altruist.Redis;

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
    private readonly IDatabase _redis;
    private readonly IMessageEncoder _encoder;
    private readonly IMessageDecoder _decoder;

    public RedisCache(IConnectionMultiplexer mux, IMessageEncoder encoder, IMessageDecoder decoder)
    {
        _redis = mux.GetDatabase();
        _encoder = encoder;
        _decoder = decoder;
    }

    public async Task<T?> GetAsync<T>(string key)
    {
        var value = await _redis.StringGetAsync(key);
        return value.HasValue ? _decoder.Decode<T>(value!) : default;
    }

    public async Task<CacheCursor<T>> GetAllAsync<T>(int batchSize = 100)
    {
        return await CreateCursorAsync<T>(string.Empty, batchSize);
    }


    public Task<CacheCursor<T>> GetAllAsync<T>()
    {
        return GetAllAsync<T>(100);
    }

    public async Task<CacheCursor<IModel>> GetAllAsync(Type type, int batchSize = 100)
    {
        return await CreateCursorAsync<IModel>(string.Empty, batchSize);
    }

    public async Task<object?> GetAsync(string key, Type type)
    {
        var value = await _redis.StringGetAsync(key);
        return value.HasValue ? _decoder.Decode(value!, type) : null;
    }

    public async Task SaveAsync<T>(string key, T entity)
    {
        var serialized = _encoder.Encode(entity as IModel);
        await _redis.StringSetAsync(key, serialized);
    }

    public async Task SaveAsync(string key, object entity, Type type)
    {
        var serialized = _encoder.Encode(entity, type);
        await _redis.StringSetAsync(key, serialized);
    }

    public async Task SaveBatchAsync<T>(Dictionary<string, T> entities)
    {
        var batch = _redis.CreateBatch();
        foreach (var (key, entity) in entities)
        {
            await batch.StringSetAsync(key, _encoder.Encode(entity as IModel));
        }
        batch.Execute();
    }

    public async Task SaveBatchAsync(Dictionary<string, object> entities, Type type)
    {
        var batch = _redis.CreateBatch();
        foreach (var (key, entity) in entities)
        {
            await batch.StringSetAsync(key, _encoder.Encode(entity, type));
        }
        batch.Execute();
    }

    public Task<CacheCursor<object>> GetAllAsync(Type type)
    {
        return CreateCursorAsync<object>(string.Empty, 100);
    }

    public async Task<bool> RemoveAsync<T>(string key)
    {
        return await _redis.KeyDeleteAsync(key);
    }

    public async Task RemoveAsync(string key, Type type)
    {
        await _redis.KeyDeleteAsync(key);
    }

    public async Task<List<string>> GetBatchKeysAsync(string baseKey, int skip, int take)
    {
        var server = _redis.Multiplexer.GetServer(_redis.Multiplexer.GetEndPoints()[0]);
        var keys = server.KeysAsync(pattern: $"{baseKey}*");

        var keyList = new List<string>();
        await foreach (var key in keys)
        {
            keyList.Add(key.ToString());
        }

        return keyList.Skip(skip).Take(take).ToList();
    }


    private Task<CacheCursor<T>> CreateCursorAsync<T>(string baseKey, int batchSize)
    {
        var cursor = new CacheCursor<T>(this, baseKey, batchSize);
        return Task.FromResult(cursor);
    }

    public async Task ClearAsync()
    {
        var server = _redis.Multiplexer.GetServer(_redis.Multiplexer.GetEndPoints()[0]);
        var keys = server.Keys(pattern: "*");
        foreach (var key in keys)
        {
            await _redis.KeyDeleteAsync(key);
        }
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

    public override async Task AddConnection(string connectionId, IConnection socket, string? roomId = null)
    {
        await base.AddConnection(connectionId, socket);
        await _redis.SetAddAsync(GlobalKey, connectionId);

        if (!string.IsNullOrEmpty(roomId))
        {
            var roomKey = $"{RoomPrefix}{roomId}";
            var existingRoom = await _cache.GetAsync<RoomPacket>(roomKey);
            existingRoom.ConnectionIds.Add(connectionId);
            await _cache.SaveAsync(roomKey, existingRoom);
        }
    }

    public override async Task RemoveConnection(string connectionId)
    {
        await base.RemoveConnection(connectionId);
        await _redis.SetRemoveAsync(GlobalKey, connectionId);

        var server = _redis.Multiplexer.GetServer(_redis.Multiplexer.GetEndPoints()[0]);
        var roomKeys = server.Keys(pattern: $"{RoomPrefix}*");

        foreach (var roomKey in roomKeys)
        {
            var roomData = await _cache.GetAsync<RoomPacket>(roomKey!);
            if (EqualityComparer<RoomPacket>.Default.Equals(roomData, default))
                continue;

            var room = roomData;
            if (room.ConnectionIds.Remove(connectionId))
            {
                if (room.ConnectionIds.Count == 0)
                    await _cache.RemoveAsync<RoomPacket>(roomKey!);
                else
                    await _cache.SaveAsync(roomKey!, room);
            }
        }
    }

    public override async Task<RoomPacket> GetRoom(string roomId)
    {
        return await _cache.GetAsync<RoomPacket>($"{RoomPrefix}{roomId}");
    }

    public override async Task<Dictionary<string, RoomPacket>> GetAllRooms()
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

    public override async Task<RoomPacket> FindAvailableRoom()
    {
        var server = _redis.Multiplexer.GetServer(_redis.Multiplexer.GetEndPoints()[0]);
        var roomKeys = server.Keys(pattern: $"{RoomPrefix}*");

        foreach (var roomKey in roomKeys)
        {
            var room = await _cache.GetAsync<RoomPacket>(roomKey!);
            if (!room.Full())
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
        await _cache.RemoveAsync<RoomPacket>($"{RoomPrefix}{roomId}");
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
