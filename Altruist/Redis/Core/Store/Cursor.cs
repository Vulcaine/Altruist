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
using System.Collections;

namespace Altruist.Redis;

public class RedisCacheCursor<T> : ICursor<T>, IEnumerable<T> where T : notnull
{
    private int BatchSize { get; }
    private int CurrentIndex { get; set; }
    private List<T> CurrentBatch { get; }

    private readonly IDatabase _redis;
    private readonly RedisDocument _document;

    private readonly string _group;

    public List<T> Items => CurrentBatch;
    public bool HasNext => CurrentBatch.Count == BatchSize;

    public RedisCacheCursor(IDatabase redis, RedisDocument document, int batchSize, string cacheGroupId = "")
    {
        _redis = redis;
        BatchSize = batchSize;
        CurrentIndex = 0;
        CurrentBatch = new List<T>();
        _document = document;
        _group = cacheGroupId;
    }

    public async Task<bool> NextBatch()
    {
        CurrentBatch.Clear();

        // Use SCAN to fetch a batch of keys matching the pattern for the type
        var server = _redis.Multiplexer.GetServer(_redis.Multiplexer.GetEndPoints().First());
        var keys = server.Keys(pattern: $"{_document.Name}{(_group != "" ? $"_{_group}" : "")}:*", pageSize: BatchSize)
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
