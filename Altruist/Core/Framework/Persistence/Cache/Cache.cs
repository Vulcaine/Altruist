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

using Altruist.InMemory;

namespace Altruist;

/// <summary>
/// Cursor over one in-memory cache bucket, returned by <see cref="InMemoryCache.GetAllAsync{T}(string)"/>.
/// Enumerate it with <c>foreach</c>; it yields a snapshot of the bucket's current values.
/// </summary>
/// <remarks>
/// Does not support batching: <see cref="NextBatch"/> throws, and <see cref="HasNext"/> stays true while the bucket is
/// non-empty (it never advances), so do not drive it with a <c>while (cursor.HasNext)</c> loop.
/// </remarks>
/// <typeparam name="T">Entry type; values are cast from the stored objects.</typeparam>
public class InMemoryCacheCursor<T> : ICursor<T>, IEnumerable<T> where T : notnull
{
    private readonly EfficientConcurrentCache<object> _source;

    /// <summary>True while the underlying bucket is non-empty (does not advance).</summary>
    public bool HasNext => _source.Count > 0;

    /// <summary>Current number of entries in the bucket.</summary>
    public int Count => _source.Count;

    /// <summary>Creates a cursor over <paramref name="source"/>.</summary>
    /// <param name="source">The cache bucket.</param>
    /// <param name="batchSize">Ignored (no batching in memory).</param>
    public InMemoryCacheCursor(EfficientConcurrentCache<object> source, int batchSize = int.MaxValue)
    {
        _source = source;
    }

    /// <summary>Not supported for in-memory cursors; enumerate with <c>foreach</c>.</summary>
    /// <returns>Never returns.</returns>
    /// <exception cref="NotImplementedException">Always.</exception>
    public Task<IEnumerable<T>> NextBatch()
    {
        // No batching needed for in-memory — just return all current values
        throw new NotImplementedException("No batching needed for in-memory — just return all current values.");
    }

    /// <summary>Enumerates a snapshot of the bucket's values.</summary>
    /// <returns>The enumerator.</returns>
    public IEnumerator<T> GetEnumerator()
    {
        if (_source == null || _source.Count == 0)
        {
            yield break;
        }

        foreach (var value in _source)
        {
            yield return (T)value!;
        }
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}

