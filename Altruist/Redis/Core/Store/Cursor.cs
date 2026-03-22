/*
Copyright 2025 Aron Gere
Licensed under the Apache License, Version 2.0
*/

using System.Text.Json;

using Altruist.Persistence;

using StackExchange.Redis;

namespace Altruist.Redis;

public class RedisCacheCursor<T> : ICursor<T>, IAsyncEnumerable<T> where T : notnull
{
    private int BatchSize { get; }
    private int CurrentIndex { get; set; }

    private readonly IDatabase _redis;
    private readonly VaultDocument _document;
    private readonly string _group;

    public bool HasNext { get; private set; } = true;
    public int Count { get; } = -1;

    public RedisCacheCursor(IDatabase redis, VaultDocument document, int batchSize, string cacheGroupId = "")
    {
        _redis = redis;
        BatchSize = batchSize;
        CurrentIndex = 0;
        _document = document;
        _group = cacheGroupId;
    }

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

    public IEnumerator<T> GetEnumerator()
        => FetchAllBatches().GetEnumerator();

    IAsyncEnumerator<T> IAsyncEnumerable<T>.GetAsyncEnumerator(CancellationToken cancellationToken)
        => GetAsyncEnumerator(cancellationToken);
}
