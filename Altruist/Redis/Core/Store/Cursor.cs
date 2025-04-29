/* 
Copyright 2025 Aron Gere

Licensed under the Apache License, Version 2.0 (the "License");
you may not use this file except in compliance with the License.
You may obtain a copy of the License at

    http://www.apache.org/licenses/LICENSE-2.0

Unless required by applicable law or agreed to in writing, software
distributed under the License is distributed on an "AS IS" BASIS,
WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
See the License for the specific language governing permissions and
limitations under the License.
*/

using System.Text.Json;
using StackExchange.Redis;

namespace Altruist.Redis;

public class RedisCacheCursor<T> : ICursor<T>, IAsyncEnumerable<T> where T : notnull
{
    private int BatchSize { get; }
    private int CurrentIndex { get; set; }

    private readonly IDatabase _redis;
    private readonly RedisDocument _document;
    private readonly string _group;

    public bool HasNext { get; private set; } = true;

    public RedisCacheCursor(IDatabase redis, RedisDocument document, int batchSize, string cacheGroupId = "")
    {
        _redis = redis;
        BatchSize = batchSize;
        CurrentIndex = 0;
        _document = document;
        _group = cacheGroupId;
    }

    public async Task<IEnumerable<T>> NextBatch()
    {
        // Use SCAN to fetch a batch of keys matching the pattern for the type
        var server = _redis.Multiplexer.GetServer(_redis.Multiplexer.GetEndPoints().First());
        var keys = server.Keys(pattern: $"{_document.Name}{(_group != "" ? $"_{_group}" : "")}:*", pageSize: BatchSize)
                        .Skip(CurrentIndex)
                        .Take(BatchSize)
                        .ToArray();

        if (keys.Length == 0)
        {
            HasNext = false;
            return Enumerable.Empty<T>();
        }

        // Fetch values for the keys in batch
        var values = await _redis.StringGetAsync(keys);
        var result = new List<T>(keys.Length);

        foreach (var value in values)
        {
            if (value.HasValue)
            {
                var entity = JsonSerializer.Deserialize<T>(value.ToString());
                if (entity != null)
                {
                    result.Add(entity);
                }
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
            {
                yield return item;
            }
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
    {
        return FetchAllBatches().GetEnumerator();
    }

    IAsyncEnumerator<T> IAsyncEnumerable<T>.GetAsyncEnumerator(CancellationToken cancellationToken)
    {
        return GetAsyncEnumerator(cancellationToken);
    }
}

