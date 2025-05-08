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
    private readonly T[] _items;
    private int CurrentIndex { get; set; }
    private int BatchSize { get; }

    public bool HasNext => CurrentIndex < _items.Length;

    public InMemoryCacheCursor(IEnumerable<T> source, int batchSize)
    {
        _items = source.ToArray();
        BatchSize = batchSize;
        CurrentIndex = 0;
    }

    public Task<IEnumerable<T>> NextBatch()
    {
        if (!HasNext)
            return Task.FromResult(Enumerable.Empty<T>());

        int remaining = _items.Length - CurrentIndex;
        int size = Math.Min(BatchSize, remaining);

        var segment = new ArraySegment<T>(_items, CurrentIndex, size);
        CurrentIndex += size;

        return Task.FromResult<IEnumerable<T>>(segment);
    }

    public IEnumerator<T> GetEnumerator()
    {
        CurrentIndex = 0;
        return ((IEnumerable<T>)_items).GetEnumerator();
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
