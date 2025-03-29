namespace Altruist;

public class CacheCursor<T>
{
    private int BatchSize { get; }
    private int CurrentIndex { get; set; }
    private int TotalItems { get; }
    private List<T> CurrentBatch { get; }

    private readonly ICache _cache;
    private readonly string _baseKey;

    public List<T> Items => CurrentBatch;
    public bool HasNext => CurrentIndex < TotalItems;

    public CacheCursor(ICache cache, string baseKey, int batchSize)
    {
        _cache = cache;
        _baseKey = baseKey;
        BatchSize = batchSize;
        CurrentIndex = 0;
        TotalItems = int.MaxValue;
        CurrentBatch = new List<T>();
    }

    public async Task<bool> NextBatch()
    {
        CurrentBatch.Clear();
        var keys = await _cache.GetBatchKeysAsync(_baseKey, CurrentIndex, BatchSize);

        if (keys.Count == 0)
            return false;

        foreach (var key in keys)
        {
            var item = await _cache.GetAsync<T>(key);
            if (item != null)
                CurrentBatch.Add(item);
        }

        CurrentIndex += keys.Count;
        return CurrentBatch.Count > 0;
    }

    public IEnumerator<T> GetEnumerator()
    {
        return CurrentBatch.GetEnumerator();
    }
}
