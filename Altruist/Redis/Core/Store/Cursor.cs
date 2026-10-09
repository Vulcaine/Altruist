/*
Copyright 2025 Aron Gere
Licensed under the Apache License, Version 2.0
*/

using System.Text.Json;

using Altruist.Persistence;

using StackExchange.Redis;

namespace Altruist.Redis;

/// <summary>
/// Lazy, forward-only cursor over the Redis entries of one document type and group (keys
/// <c>{document}[_{group}]:*</c>), returned by <see cref="RedisCacheProvider.GetAllRemoteAsync{T}(int, string)"/>.
/// Supports <c>foreach</c> (blocking) and <c>await foreach</c>.
/// </summary>
/// <remarks>
/// Each batch re-runs <c>SCAN</c> on the first endpoint from the start and skips the keys already
/// returned, then reads the page with one <c>MGET</c>: cost grows with position, and keys added or
/// removed during iteration can be skipped or repeated (no snapshot). Values are deserialized as
/// <typeparamref name="T"/> (no type discriminator). Single use and not thread-safe.
/// </remarks>
/// <typeparam name="T">Model type.</typeparam>
public class RedisCacheCursor<T> : ICursor<T>, IAsyncEnumerable<T> where T : notnull
{
    private int BatchSize { get; }
    private int CurrentIndex { get; set; }

    private readonly IDatabase _redis;
    private readonly VaultDocument _document;
    private readonly string _group;

    /// <summary>
    /// <c>true</c> until a batch comes back empty or with fewer than the batch size of values (a batch with
    /// missing/expired values also ends iteration).
    /// </summary>
    public bool HasNext { get; private set; } = true;
    /// <summary>Always <c>-1</c>: the total is unknown.</summary>
    public int Count { get; } = -1;

    /// <summary>Creates the cursor; no Redis call is made until the first batch.</summary>
    /// <param name="redis">Database to read from.</param>
    /// <param name="document">Document mapping that supplies the key prefix.</param>
    /// <param name="batchSize">Keys per batch (also the SCAN page size).</param>
    /// <param name="cacheGroupId">Optional group.</param>
    public RedisCacheCursor(IDatabase redis, VaultDocument document, int batchSize, string cacheGroupId = "")
    {
        _redis = redis;
        BatchSize = batchSize;
        CurrentIndex = 0;
        _document = document;
        _group = cacheGroupId;
    }

    /// <summary>Fetches the next batch (network I/O); returns an empty sequence and clears <see cref="HasNext"/> when exhausted.</summary>
    public async Task<IEnumerable<T>> NextBatch()
    {
        var server = _redis.Multiplexer.GetServer(_redis.Multiplexer.GetEndPoints().First());
        var keys = server.Keys(
                pattern: $"{_document.Name}{(_group != "" ? $"_{_group}" : "")}:*",
                pageSize: BatchSize)
            .Skip(CurrentIndex)
            .Take(BatchSize)
            .ToArray();

        if (keys.Length == 0)
        {
            HasNext = false;
            return Enumerable.Empty<T>();
        }

        var values = await _redis.StringGetAsync(keys);
        var result = new List<T>(keys.Length);

        foreach (var value in values)
        {
            if (value.HasValue)
            {
                var entity = JsonSerializer.Deserialize<T>(value.ToString());
                if (entity != null)
                    result.Add(entity);
            }
        }

        CurrentIndex += keys.Length;
        HasNext = result.Count == BatchSize;
        return result;
    }

    private IEnumerable<T> FetchAllBatches()
    {
        while (true)
        {
            if (!HasNext)
                yield break;

            var batch = NextBatch().GetAwaiter().GetResult();
            foreach (var item in batch)
                yield return item;
        }
    }

    /// <summary>Iterates all remaining entries batch by batch.</summary>
    /// <param name="cancellationToken">Not observed.</param>
    public async IAsyncEnumerator<T> GetAsyncEnumerator(CancellationToken cancellationToken = default)
    {
        while (true)
        {
            if (!HasNext)
                yield break;

            var batch = await NextBatch();
            foreach (var item in batch)
                yield return item;
        }
    }

    /// <summary>Iterates all remaining entries, blocking on each batch (sync-over-async; prefer <c>await foreach</c>).</summary>
    public IEnumerator<T> GetEnumerator()
        => FetchAllBatches().GetEnumerator();

    IAsyncEnumerator<T> IAsyncEnumerable<T>.GetAsyncEnumerator(CancellationToken cancellationToken)
        => GetAsyncEnumerator(cancellationToken);
}
