using Altruist.Socket;
using Altruist.Web;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;
using System.Text.Json;
using System.Text;
using Altruist.Contracts;

namespace Altruist.Redis;

public interface IAltruistRedisConnectionProvider : IConnectionStore
{
    RedisResult ScriptEvaluate(string script, RedisKey[]? keys = null, RedisValue[]? values = null, CommandFlags flags = CommandFlags.None);
}

public sealed class RedisCacheProvider : IRedisCacheProvider
{
    private readonly IDatabase _redis;
    private readonly Dictionary<Type, RedisDocument> _documents = new();

    private readonly Dictionary<string, RedisDocument> _typeLookup = new();

    public RedisCacheProvider(IConnectionMultiplexer mux)
    {
        _redis = mux.GetDatabase();
        var documentHelper = new RedisDocumentHelper(mux);
        var documents = documentHelper.CreateDocuments();

        _documents = documents
            .GroupBy(doc => doc.Type)
            .ToDictionary(g => g.Key, g => g.Last());

        _typeLookup = documents
            .GroupBy(doc => doc.Name)
            .ToDictionary(g => g.Key, g => g.Last());

        HookRedisEvents();
    }


    public IDatabase GetDatabase()
    {
        return _redis;
    }

    private RedisDocument GetDocumentOrFail<T>()
    {
        if (_documents.TryGetValue(typeof(T), out var document) && document != null)
        {
            return document;
        }
        else
        {
            throw new KeyNotFoundException("Document not found for type " + typeof(T).Name);
        }
    }


    #region Connection Events
    private event Action? _onConnected = () => { };
    private event Action<Exception>? _onRetryExhausted = _ => { };

    private event Action<Exception>? _onFailed = _ => { };

    public event Action? OnConnected
    {
        add => _onConnected += value;
        remove => _onConnected -= value;
    }

    public event Action<Exception>? OnRetryExhausted
    {
        add => _onRetryExhausted += value;
        remove => _onRetryExhausted -= value;
    }

    public event Action<Exception>? OnFailed
    {
        add => _onFailed += value;
        remove => _onFailed -= value;
    }


    public void RaiseConnectedEvent()
    {
        _onConnected?.Invoke();
    }

    public void RaiseFailedEvent(Exception ex)
    {
        _onFailed?.Invoke(ex);
    }

    public void RaiseOnRetryExhaustedEvent(Exception ex)
    {
        _onRetryExhausted?.Invoke(ex);
    }

    private void HookRedisEvents()
    {
        _redis.Multiplexer.ConnectionRestored += (_, args) =>
        {
            if (args.ConnectionType == ConnectionType.Interactive)
            {
                RaiseConnectedEvent();
            }
        };
        _redis.Multiplexer.ConnectionFailed += (_, args) =>
        {
            if (args.ConnectionType == ConnectionType.Interactive)
            {
                RaiseFailedEvent(args.Exception ?? new Exception("Connection failed"));
            }
        };

        if (_redis.Multiplexer.IsConnected) RaiseConnectedEvent();
        else _onRetryExhausted?.Invoke(new Exception("Connection failed"));
    }

    public bool IsConnected => _redis.Multiplexer.IsConnected;

    #endregion

    #region Redis API

    private static ThreadLocal<MemoryStream> _memoryStream = new(() => new MemoryStream());
    public ICacheServiceToken Token => RedisCacheServiceToken.Instance;

    public string ServiceName { get; } = "RedisCache";

    private async Task SaveObjectAsync<T>(string key, T entity, string group = "") where T : notnull
    {
        var document = GetDocumentOrFail<T>();
        var memoryStream = _memoryStream.Value!;
        memoryStream.Seek(0, SeekOrigin.Begin);
        memoryStream.SetLength(0);

        using (var writer = new Utf8JsonWriter(memoryStream, new JsonWriterOptions { SkipValidation = true }))
        {
            JsonSerializer.Serialize(writer, entity);
        }

        await _redis.StringSetAsync($"{document.Name}{(string.IsNullOrEmpty(group) ? "" : $"_{group}")}:{key}", memoryStream.ToArray());
    }

    private async Task<T?> GetObjectAsync<T>(string key, string group = "")
    {
        var document = GetDocumentOrFail<T>();
        var json = await _redis.StringGetAsync($"{document.Name}{(string.IsNullOrEmpty(group) ? "" : $"_{group}")}:{key}");
        if (json.IsNullOrEmpty) return default;

        ReadOnlyMemory<byte> jsonMemory = Encoding.UTF8.GetBytes(json.ToString()).AsMemory();

        var jsonSpan = jsonMemory.Span;
        var reader = new Utf8JsonReader(jsonSpan);

        string? typeInfo = null;

        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.PropertyName && reader.ValueTextEquals(document.TypePropertyName))
            {
                reader.Read();
                typeInfo = reader.GetString();
                break;
            }
        }

        if (typeInfo == null)
        {
            throw new TypeAccessException("Cannot deserialize redis data. Could not find type info for document " + typeof(T).Name + ". Make sure your model contains Type property. The type property we found in model: " + document.TypePropertyName);
        }

        if (typeInfo == null || !_typeLookup.TryGetValue(typeInfo, out var typeDoc))
            throw new TypeAccessException("Cannot deserialize redis data. Could not find document for type " + typeInfo + ". Make sure it is registered.");

        return (T)JsonSerializer.Deserialize(jsonSpan, typeDoc.Type)!;
    }

    public async Task<T?> GetAsync<T>(string key, string group = "") where T : notnull
    {
        return await GetObjectAsync<T>(key, group);
    }

    public async Task SaveAsync<T>(string key, T entity, string group = "") where T : notnull
    {
        await SaveObjectAsync(key, entity, group);
    }

    public async Task SaveBatchAsync<T>(Dictionary<string, T> entities, string group = "") where T : notnull
    {
        var batch = _redis.CreateBatch();
        var tasks = new List<Task>();

        foreach (var (key, entity) in entities)
        {
            var serializedEntity = JsonSerializer.Serialize(entity);
            tasks.Add(batch.StringSetAsync($"{key}{(string.IsNullOrEmpty(group) ? "" : $"_{group}")} ", serializedEntity));
        }

        await Task.WhenAll(tasks);
    }


    public async Task<T?> RemoveAsync<T>(string key, string group = "") where T : notnull
    {
        var entity = await GetObjectAsync<T>(key);
        if (entity != null)
        {
            _ = RemoveAndForgetAsync<T>(key, group);
        }
        return entity;
    }

    public async Task ClearAsync<T>(string group = "") where T : notnull
    {
        var server = _redis.Multiplexer.GetServer(_redis.Multiplexer.GetEndPoints().First());
        var document = GetDocumentOrFail<T>();
        var keys = server.Keys(pattern: $"{document.Name}{(string.IsNullOrEmpty(group) ? "" : $"_{group}")}:*").ToArray();

        if (keys.Length > 0)
        {
            await _redis.KeyDeleteAsync(keys);
        }
    }


    public Task<ICursor<T>> GetAllAsync<T>(int batchSize = 100, string group = "") where T : notnull
    {
        var cursor = new RedisCacheCursor<T>(_redis, GetDocumentOrFail<T>(), batchSize, group);
        return Task.FromResult(cursor as ICursor<T>);
    }

    public Task<bool> ContainsAsync<T>(string key, string group = "") where T : notnull
    {
        var document = GetDocumentOrFail<T>();
        return _redis.KeyExistsAsync($"{document.Name}{(string.IsNullOrEmpty(group) ? "" : $"_{group}")}:{key}");
    }

    public async Task<ICursor<T>> GetAllAsync<T>(string group = "") where T : notnull
    {
        return await GetAllAsync<T>(100, group);
    }

    public Task<ICursor<object>> GetAllAsync(Type type, string group = "")
    {
        if (!_documents.TryGetValue(type, out var document))
        {
            throw new InvalidOperationException($"Document mapping for type {type.Name} not found.");
        }

        var cursorType = typeof(RedisCacheCursor<>).MakeGenericType(type);
        var cursor = Activator.CreateInstance(cursorType, _redis, document, 100, group);

        return Task.FromResult((cursor as ICursor<object>)!) ?? throw new InvalidOperationException("Failed to create cursor.");
    }

    public async Task ClearAllAsync()
    {
        foreach (var document in _documents.Values)
        {
            var keys = (await Keys($"{document.Name}:*")).ToArray();
            if (keys.Length > 0)
            {
                await _redis.KeyDeleteAsync(keys);
            }
        }
    }

    public async Task RemoveAndForgetAsync<T>(string key, string group = "") where T : notnull
    {
        var document = GetDocumentOrFail<T>();
        await _redis.KeyDeleteAsync($"{document.Name}{(string.IsNullOrEmpty(group) ? "" : $"_{group}")}:{key}");
    }

    public async Task<IEnumerable<RedisKey>> Keys(string pattern)
    {
        var server = _redis.Multiplexer.GetServer(_redis.Multiplexer.GetEndPoints()[0]);

        var keys = new List<RedisKey>();

        await foreach (var key in server.KeysAsync(pattern: pattern))
        {
            keys.Add(key);
        }

        return keys;
    }


    public async Task<IEnumerable<RedisKey>> KeysAsync<T>() where T : notnull
    {
        var document = GetDocumentOrFail<T>();
        return await Keys(pattern: $"{document.Name}:*");
    }

    public Task ConnectAsync(int maxRetries, int delayMilliseconds)
    {
        throw new NotImplementedException("RedisConnectionService.ConnectAsync() is not implemented. It is done automatically via the Multiplexer.");
    }

    #endregion
}

#region Connection Service

public sealed class RedisConnectionService : AbstractConnectionStore, IAltruistRedisConnectionProvider
{
    private readonly RedisCacheProvider _cache;
    private readonly IDatabase _redis;

    public RedisConnectionService(IConnectionMultiplexer redis,
        IMemoryCacheProvider memoryCache,
        RedisCacheProvider cache, ILoggerFactory loggerFactory) : base(memoryCache, loggerFactory)
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

        return null;
    }

    public override async Task<bool> IsConnectionExistsAsync(string connectionId) => await _cache.ContainsAsync<Connection>(connectionId);

    public override async Task<bool> AddConnectionAsync(string connectionId, Connection socket, string? roomId = null)
    {
        if (!await base.AddConnectionAsync(connectionId, socket))
        {
            return false;
        }

        await _cache.SaveAsync(connectionId, socket);

        if (!string.IsNullOrEmpty(roomId))
        {
            var existingRoom = await _cache.GetAsync<RoomPacket>(roomId);
            if (existingRoom != null)
            {
                existingRoom.ConnectionIds.Add(connectionId);
                await _cache.SaveAsync(roomId, existingRoom);
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
        await _cache.RemoveAndForgetAsync<Connection>(connectionId);
        var roomKeys = (await _cache.KeysAsync<RoomPacket>()).ToArray();

        foreach (var roomKey in roomKeys)
        {
            var room = await _cache.GetAsync<RoomPacket>(roomKey!);
            if (room == null)
                continue;

            if (room.ConnectionIds.Remove(connectionId) || room.Empty())
            {
                if (room.Empty())
                    await _cache.RemoveAndForgetAsync<RoomPacket>(roomKey!);
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
        var roomKeys = await _cache.KeysAsync<RoomPacket>();

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
        var roomKeys = await _cache.KeysAsync<RoomPacket>();

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
        await _cache.SaveAsync(newRoom.Id, newRoom);
        return newRoom;
    }

    public override async Task SaveRoomAsync(RoomPacket roomPacket)
    {
        await _cache.SaveAsync(roomPacket.Id, roomPacket);
    }

    public override async Task DeleteRoomAsync(string roomId)
    {
        await _cache.RemoveAndForgetAsync<RoomPacket>(roomId);
    }

    public RedisResult ScriptEvaluate(string script, RedisKey[]? keys = null, RedisValue[]? values = null, CommandFlags flags = CommandFlags.None)
    {
        return _redis.ScriptEvaluate(script, keys, values, flags);
    }
}

#endregion