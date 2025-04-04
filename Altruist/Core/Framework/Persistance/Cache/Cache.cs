using System.Collections;

namespace Altruist;


public class InMemoryCacheCursor<T> : ICursor<T>, IEnumerable<T> where T : notnull
{
    private int BatchSize { get; }
    private int CurrentIndex { get; set; }
    private int TotalItems { get; }
    private List<T> CurrentBatch { get; }
    private readonly Dictionary<string, object> _cache;

    public List<T> Items => CurrentBatch;
    public bool HasNext => CurrentIndex < TotalItems;

    public InMemoryCacheCursor(Dictionary<string, object> cache, int batchSize)
    {
        _cache = cache;
        BatchSize = batchSize;
        CurrentIndex = 0;
        TotalItems = _cache.Values.Count;
        CurrentBatch = new List<T>();
    }

    public Task<bool> NextBatch()
    {
        CurrentBatch.Clear();
        var entities = _cache.Values.OfType<T>().Skip(CurrentIndex).Take(BatchSize).ToList();

        if (entities.Count == 0)
            return Task.FromResult(false);

        CurrentBatch.AddRange(entities);
        CurrentIndex += entities.Count;

        return Task.FromResult(true);
    }

    public IEnumerator<T> GetEnumerator()
    {
        return FetchAllBatches().GetEnumerator();
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

    IEnumerator IEnumerable.GetEnumerator()
    {
        return GetEnumerator();
    }
}
