using System.Collections;
using Altruist.Socket;
using Altruist.Web;
using Microsoft.Extensions.Logging;
using Redis.OM;
using StackExchange.Redis;
using System.Text.Json.Serialization;
using System.Text.Json;

namespace Altruist.Redis;

public class RedisCacheCursor<T> : ICacheCursor<T>, IEnumerable<T> where T : notnull
{
    private int BatchSize { get; }
    private int CurrentIndex { get; set; }
    private List<T> CurrentBatch { get; }

    private readonly IDatabase _redis;
    private readonly RedisDocument _document;

    public List<T> Items => CurrentBatch;
    public bool HasNext => CurrentBatch.Count == BatchSize;

    public RedisCacheCursor(IDatabase redis, RedisDocument document, int batchSize)
    {
        _redis = redis;
        BatchSize = batchSize;
        CurrentIndex = 0;
        CurrentBatch = new List<T>();
        _document = document;
    }

    public async Task<bool> NextBatch()
    {
        CurrentBatch.Clear();

        // Use SCAN to fetch a batch of keys matching the pattern for the type
        var server = _redis.Multiplexer.GetServer(_redis.Multiplexer.GetEndPoints().First());
        var keys = server.Keys(pattern: $"{_document.Name}:*", pageSize: BatchSize)
                        .Skip(CurrentIndex)
                        .Take(BatchSize)
                        .ToArray();

        if (keys.Length == 0)
            return false;

        // Fetch values for the keys in batch
        var values = await _redis.StringGetAsync(keys);

        foreach (var value in values)
        {
            if (value.HasValue)
            {
                var entity = JsonSerializer.Deserialize<T>(value.ToString());
                if (entity != null)
                {
                    CurrentBatch.Add(entity);
                }
            }
        }

        CurrentIndex += keys.Length;
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
    // Task<bool> CreateIndexAsync(Type type);
    // IRedisCollection<TNonNullType> RedisCollection<TNonNullType>() where TNonNullType : notnull;
    RedisResult ScriptEvaluate(string script, RedisKey[]? keys = null, RedisValue[]? values = null, CommandFlags flags = CommandFlags.None);
}

public sealed class RedisCache : IAltruistRedisProvider
{
    private readonly IDatabase _redis;
    private readonly Dictionary<Type, RedisDocument> _documents = new();

    public RedisCache(IConnectionMultiplexer mux)
    {
        _redis = mux.GetDatabase();
        var documentHelper = new RedisDocumentHelper(mux);
        var documents = documentHelper.CreateDocuments();
        _documents = documents.ToDictionary(doc => doc.Type);
    }

    private RedisDocument GetDocumentOrFail<T>()
    {
        var document = _documents[typeof(T)];
        if (document == null) throw new KeyNotFoundException("Document not found for type " + typeof(T).Name);
        return document;
    }

    private async Task SaveObjectAsync<T>(string key, T entity) where T : notnull
    {
        var document = GetDocumentOrFail<T>();
        var serializedEntity = JsonSerializer.Serialize(entity);
        await _redis.StringSetAsync($"{document.Name}:{key}", serializedEntity);
    }

    private async Task<T?> GetObjectAsync<T>(string key) where T : notnull
    {
        var serializedEntity = await _redis.StringGetAsync($"{typeof(T).Name}:{key}");
        if (serializedEntity.IsNullOrEmpty)
            return default;

        return JsonSerializer.Deserialize<T>(serializedEntity!);
    }

    // Retrieve object from Redis using a general key
    public async Task<T?> GetAsync<T>(string key) where T : notnull
    {
        return await GetObjectAsync<T>(key);
    }

    public async Task SaveAsync<T>(string key, T entity) where T : notnull
    {
        await SaveObjectAsync(key, entity);
    }

    public async Task SaveBatchAsync<T>(Dictionary<string, T> entities) where T : notnull
    {
        var batch = _redis.CreateBatch();
        var tasks = new List<Task>();

        foreach (var (key, entity) in entities)
        {
            var serializedEntity = JsonSerializer.Serialize(entity);
            tasks.Add(batch.StringSetAsync(key, serializedEntity));
        }

        await Task.WhenAll(tasks);
    }


    public async Task<T?> RemoveAsync<T>(string key) where T : notnull
    {
        var entity = await GetObjectAsync<T>(key);
        if (entity != null)
        {
            var document = GetDocumentOrFail<T>();
            await _redis.KeyDeleteAsync($"{document.Name}:{key}");
        }
        return entity;
    }

    public async Task ClearAsync<T>() where T : notnull
    {
        var server = _redis.Multiplexer.GetServer(_redis.Multiplexer.GetEndPoints().First());
        var document = GetDocumentOrFail<T>();
        var keys = server.Keys(pattern: $"{document.Name}:*").ToArray();

        if (keys.Length > 0)
        {
            await _redis.KeyDeleteAsync(keys);
        }
    }


    public Task<ICacheCursor<T>> GetAllAsync<T>(int batchSize = 100) where T : notnull
    {
        var cursor = new RedisCacheCursor<T>(_redis, GetDocumentOrFail<T>(), batchSize);
        return Task.FromResult(cursor as ICacheCursor<T>);
    }

    public Task<bool> ContainsAsync<T>(string key) where T : notnull
    {
        return _redis.KeyExistsAsync(key);
    }

    public async Task<ICacheCursor<T>> GetAllAsync<T>() where T : notnull
    {
        return await GetAllAsync<T>(100);
    }

    public Task<ICacheCursor<object>> GetAllAsync(Type type)
    {
        if (!_documents.TryGetValue(type, out var document))
        {
            throw new InvalidOperationException($"Document mapping for type {type.Name} not found.");
        }

        var cursorType = typeof(RedisCacheCursor<>).MakeGenericType(type);
        var cursor = Activator.CreateInstance(cursorType, _redis, document, 100);

        return Task.FromResult((cursor as ICacheCursor<object>)!) ?? throw new InvalidOperationException("Failed to create cursor.");
    }

    public async Task ClearAllAsync()
    {
        var server = _redis.Multiplexer.GetServer(_redis.Multiplexer.GetEndPoints().First());

        foreach (var document in _documents.Values)
        {
            var keys = server.Keys(pattern: $"{document.Name}:*").ToArray();
            if (keys.Length > 0)
            {
                await _redis.KeyDeleteAsync(keys);
            }
        }
    }
}



// public sealed class RedisCache : IAltruistRedisProvider
// {
//     private readonly RedisConnectionProvider _provider;
//     private readonly IDatabase _redis;

//     private readonly Dictionary<Type, object> _redisCollections = new();

//     public RedisCache(
//         RedisConnectionProvider provider,
//         IConnectionMultiplexer mux)
//     {
//         _redis = mux.GetDatabase();
//         _provider = provider;
//     }

//     private IRedisCollection<T> GetCollection<T>() where T : notnull
//     {
//         var type = typeof(T);

//         if (!_redisCollections.TryGetValue(type, out var collection))
//         {
//             collection = _provider.RedisCollection<T>();
//             _redisCollections[type] = collection;
//         }

//         return (IRedisCollection<T>)collection;
//     }

//     public async Task<T?> GetAsync<T>(string key) where T : notnull
//     {
//         var repo = GetCollection<T>();
//         var entity = await repo.FindByIdAsync(key);
//         return entity;
//     }

//     public Task<ICacheCursor<T>> GetAllAsync<T>(int batchSize = 100) where T : notnull
//     {
//         var cursor = new RedisCacheCursor<T>(_provider, batchSize);
//         return Task.FromResult(cursor as ICacheCursor<T>);
//     }

//     public Task<object?> GetAsync(string key, Type type)
//     {
//         throw new NotSupportedException("GetAsync(string key, Type type) is not supported when using Redis OM.");
//     }

//     public async Task SaveAsync<T>(string key, T entity) where T : notnull
//     {
//         var repo = GetCollection<T>();
//         await repo.InsertAsync(entity);
//         await repo.SaveAsync();
//     }

//     public Task SaveAsync(string key, object entity, Type type)
//     {
//         throw new NotSupportedException("SaveAsync(string key, object entity, Type type) is not supported when using Redis OM.");
//     }

//     public async Task SaveBatchAsync<T>(Dictionary<string, T> entities) where T : notnull
//     {
//         var repo = GetCollection<T>();
//         foreach (var (key, entity) in entities)
//         {
//             await repo.InsertAsync(entity);
//         }
//         await repo.SaveAsync();
//     }

//     public Task SaveBatchAsync(Dictionary<string, object> entities, Type type)
//     {
//         throw new NotSupportedException("SaveBatchAsync(Dictionary<string, object> entities, Type type) is not supported when using Redis OM.");
//     }

//     public async Task<T?> RemoveAsync<T>(string key) where T : notnull
//     {
//         var repo = GetCollection<T>();

//         var element = await repo.FindByIdAsync(key);
//         if (element != null)
//         {
//             await repo.DeleteAsync(element);
//         }

//         return element;
//     }

//     public Task RemoveAsync(string key, Type type)
//     {
//         throw new NotSupportedException("RemoveAsync(string key, Type type) is not supported when using Redis OM.");
//     }

//     public async Task ClearAsync<T>() where T : notnull
//     {
//         var repo = GetCollection<T>();
//         var allItems = await repo.ToListAsync();
//         await repo.DeleteAsync(allItems);
//     }

//     public async Task<ICacheCursor<T>> GetAllAsync<T>() where T : notnull
//     {
//         return await CreateCursorAsync<T>(100);
//     }

//     public Task<ICacheCursor<object>> GetAllAsync(Type type)
//     {
//         throw new NotSupportedException("GetAllAsync(Type type) is not supported when using Redis OM.");
//     }

//     public Task ClearAllAsync()
//     {
//         throw new NotSupportedException("ClearAllAsync() is not supported when using Redis OM.");
//     }

//     private Task<ICacheCursor<T>> CreateCursorAsync<T>(int batchSize) where T : notnull
//     {
//         var cursor = new RedisCacheCursor<T>(_provider, batchSize);
//         return Task.FromResult(cursor as ICacheCursor<T>);
//     }

//     public Task<bool> ContainsAsync<T>(string key) where T : notnull
//     {
//         throw new NotSupportedException("ContainsAsync<T>(string key) is not supported when using Redis OM.");
//     }
// }

public sealed class RedisConnectionService : AbstractConnectionStore, IAltruistRedisConnectionProvider
{
    private readonly RedisCache _cache;
    private readonly IDatabase _redis;
    // private readonly RedisConnectionProvider _provider;
    private const string RoomPrefix = "room:";

    public RedisConnectionService(IConnectionMultiplexer redis,
        IMemoryCache memoryCache,
        RedisCache cache, ILoggerFactory loggerFactory) : base(memoryCache, loggerFactory)
    {
        _redis = redis.GetDatabase();
        _cache = cache;
    }

    public override async Task<Connection?> GetConnectionAsync(string connectionId)
    {
        var inMemoryConn = await base.GetConnectionAsync(connectionId);

        if (inMemoryConn != null)
        {
            return inMemoryConn;
        }

        var conn = await _cache.GetAsync<Connection>(connectionId);

        if (conn != null && conn.Type == "websocket")
        {
            return new CachedWebSocketConnection(conn);
        }
        else if (conn != null && conn.Type == "udp")
        {
            return new CachedUdpConnection(conn);
        }
        else if (conn != null && conn.Type == "tcp")
        {
            return new CachedTcpConnection(conn);
        }

        throw new Exception($"Unknown connection type {conn?.Type}.");
    }

    public override async Task<bool> IsConnectionExistsAsync(string connectionId) => await _cache.ContainsAsync<Connection>(connectionId);

    public override async Task<bool> AddConnectionAsync(string connectionId, Connection socket, string? roomId = null)
    {
        if (!await base.AddConnectionAsync(connectionId, socket))
        {
            return false;
        }

        Connection conn = socket; // force downcast
        await _cache.SaveAsync(connectionId, conn);

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

    public override async Task RemoveConnectionAsync(string connectionId)
    {
        await base.RemoveConnectionAsync(connectionId);
        await _cache.RemoveAsync<Connection>(connectionId);

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

        return await CreateRoomAsync();
    }

    public override async Task<RoomPacket> CreateRoomAsync()
    {
        var newRoom = await base.CreateRoomAsync();
        await _cache.SaveAsync($"{RoomPrefix}{newRoom.Id}", newRoom);
        return newRoom;
    }

    public override async Task SaveRoomAsync(RoomPacket roomPacket)
    {
        await _cache.SaveAsync($"{RoomPrefix}{roomPacket.Id}", roomPacket);
    }

    public override async Task DeleteRoomAsync(string roomId)
    {
        await _cache.RemoveAsync<RoomPacket>(roomId);
    }

    // public IRedisCollection<TNonNullClass> RedisCollection<TNonNullClass>() where TNonNullClass : notnull
    // {
    //     return _provider.RedisCollection<TNonNullClass>();
    // }

    public RedisResult ScriptEvaluate(string script, RedisKey[]? keys = null, RedisValue[]? values = null, CommandFlags flags = CommandFlags.None)
    {
        return _redis.ScriptEvaluate(script, keys, values, flags);
    }

    // public async Task<bool> CreateIndexAsync(Type type)
    // {
    //     return await _provider.Connection.CreateIndexAsync(type);
    // }
}
