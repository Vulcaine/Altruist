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

    public RedisCacheCursor(IDatabase redis, RedisDocument document, int batchSize, string group = "")
    {
        _redis = redis;
        BatchSize = batchSize;
        CurrentIndex = 0;
        CurrentBatch = new List<T>();
        _document = document;
        _group = group;
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
