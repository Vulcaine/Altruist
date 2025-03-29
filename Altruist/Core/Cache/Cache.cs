using System.Collections.Concurrent;

namespace Altruist;


public class InMemoryCacheCursor<T> : ICacheCursor<T> where T : notnull
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
        TotalItems = int.MaxValue; // You can adjust this as needed
        CurrentBatch = new List<T>();
    }

    public Task<bool> NextBatch()
    {
        CurrentBatch.Clear();
        var entities = _cache.Values.Select(list => list).OfType<T>().Skip(CurrentIndex).Take(BatchSize).ToList();

        if (entities.Count == 0)
            return Task.FromResult(false);

        CurrentBatch.AddRange(entities);
        CurrentIndex += entities.Count;

        return Task.FromResult(true);
    }

    public IEnumerator<T> GetEnumerator()
    {
        return CurrentBatch.GetEnumerator();
    }
}
