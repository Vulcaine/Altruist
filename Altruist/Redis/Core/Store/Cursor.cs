/*
Copyright 2025 Aron Gere
Licensed under the Apache License, Version 2.0
*/

using StackExchange.Redis;

namespace Altruist.Redis;

/// <summary>
/// Lazy, forward-only cursor over the Redis entries of one document type and group (keys
/// <c>{document}[_{group}]:*</c>), returned by <see cref="RedisCacheProvider.GetAllRemoteAsync{T}(int, string)"/>.
/// Supports <c>foreach</c> (blocking) and <c>await foreach</c>.
/// </summary>
/// <remarks>
/// One <c>SCAN</c> pass (on every primary endpoint) runs across the whole iteration; each batch takes the next
/// keys from it and reads their values with one <c>MGET</c>, so a full iteration costs one keyspace scan plus one
/// <c>MGET</c> per batch. Keys whose value disappeared between the scan and the read are skipped, and a key the scan
/// reports twice is returned once. Values are deserialized like <see cref="RedisCacheProvider.GetRemoteAsync{T}"/>
/// (honouring the type discriminator). Not a snapshot: keys added or removed during iteration may or may not be
/// seen. Single use and not thread-safe.
/// </remarks>
/// <typeparam name="T">Model type.</typeparam>
public sealed class RedisCacheCursor<T> : ICursor<T>, IAsyncEnumerable<T> where T : notnull
{
    private readonly IDatabase _redis;
    private readonly Func<CancellationToken, IAsyncEnumerable<RedisKey>> _scan;
    private readonly Func<RedisValue, T?> _deserialize;
    private readonly int _batchSize;
    private readonly HashSet<RedisKey> _seen = new();
    private IAsyncEnumerator<RedisKey>? _keys;

    /// <summary>
    /// <c>true</c> until the scan is exhausted. The last batch may be shorter than the batch size, or empty when the
    /// scan ended exactly at a batch boundary.
    /// </summary>
    public bool HasNext { get; private set; } = true;

    /// <summary>Always <c>-1</c>: the total is unknown.</summary>
    public int Count => -1;

    internal RedisCacheCursor(IDatabase redis, Func<CancellationToken, IAsyncEnumerable<RedisKey>> scan, int batchSize,
        Func<RedisValue, T?> deserialize)
    {
        _redis = redis;
        _scan = scan;
        _batchSize = batchSize;
        _deserialize = deserialize;
    }

    /// <summary>Fetches the next batch (network I/O); returns an empty sequence and clears <see cref="HasNext"/> when exhausted.</summary>
    public Task<IEnumerable<T>> NextBatch() => NextBatchAsync(CancellationToken.None);

    private async Task<IEnumerable<T>> NextBatchAsync(CancellationToken cancellationToken)
    {
        if (!HasNext)
            return Array.Empty<T>();

        _keys ??= _scan(cancellationToken).GetAsyncEnumerator(cancellationToken);
        var keys = new List<RedisKey>(_batchSize);
        while (keys.Count < _batchSize)
        {
            if (!await _keys.MoveNextAsync().ConfigureAwait(false))
            {
                HasNext = false;
                await _keys.DisposeAsync().ConfigureAwait(false);
                break;
            }
            if (_seen.Add(_keys.Current))
                keys.Add(_keys.Current);
        }

        if (keys.Count == 0)
            return Array.Empty<T>();

        var values = await _redis.StringGetAsync(keys.ToArray()).ConfigureAwait(false);
        var result = new List<T>(keys.Count);
        foreach (var value in values)
        {
            if (value.IsNullOrEmpty)
                continue;
            if (_deserialize(value) is { } entity)
                result.Add(entity);
        }
        return result;
    }

    private IEnumerable<T> FetchAllBatches()
    {
        while (HasNext)
        {
            foreach (var item in NextBatchAsync(CancellationToken.None).GetAwaiter().GetResult())
                yield return item;
        }
    }

    /// <summary>Iterates all remaining entries batch by batch.</summary>
    /// <param name="cancellationToken">Stops the scan between pages.</param>
    public async IAsyncEnumerator<T> GetAsyncEnumerator(CancellationToken cancellationToken = default)
    {
        while (HasNext)
        {
            foreach (var item in await NextBatchAsync(cancellationToken).ConfigureAwait(false))
                yield return item;
        }
    }

    /// <summary>Iterates all remaining entries, blocking on each batch (sync-over-async; prefer <c>await foreach</c>).</summary>
    public IEnumerator<T> GetEnumerator()
        => FetchAllBatches().GetEnumerator();
}
