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

using System.Collections;

namespace Altruist;

public class InMemoryCacheCursor<T> : ICursor<T>, IEnumerable<T> where T : notnull
{
    private int BatchSize { get; }
    private int CurrentIndex { get; set; }
    private int TotalItems => _cache.Values.Count();
    private readonly Dictionary<string, object> _cache;

    public bool HasNext => CurrentIndex < TotalItems;

    public InMemoryCacheCursor(Dictionary<string, object> cache, int batchSize)
    {
        _cache = cache;
        BatchSize = batchSize;
        CurrentIndex = 0;
    }

    public Task<IEnumerable<T>> NextBatch()
    {
        var batch = _cache.Values.OfType<T>()
                      .Skip(CurrentIndex)
                      .Take(BatchSize);

        var size = batch.Count();

        if (size == 0)
            return Task.FromResult(Enumerable.Empty<T>());

        CurrentIndex += size;
        return Task.FromResult(batch);
    }

    public IEnumerator<T> GetEnumerator()
    {
        return FetchAllBatches().GetEnumerator();
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

    IEnumerator IEnumerable.GetEnumerator()
    {
        return GetEnumerator();
    }
}
