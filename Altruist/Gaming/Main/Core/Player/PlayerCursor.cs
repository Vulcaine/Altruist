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

using Altruist.Gaming;

namespace Altruist;

public class PlayerCursor<T> : ICursor<T>, IAsyncEnumerable<T> where T : notnull, PlayerEntity
{
    private ICursor<T> _underlying;
    public bool HasNext => _underlying.HasNext;

    public List<T> Items => _underlying.Items;

    public PlayerCursor(ICacheProvider cache)
    {
        _underlying = cache.GetAllAsync<T>().GetAwaiter().GetResult();
    }

    public async Task<bool> NextBatch()
    {
        return await _underlying.NextBatch();
    }

    public async IAsyncEnumerator<T> GetAsyncEnumerator(CancellationToken cancellationToken = default)
    {
        var firstBatchFetched = await NextBatch();

        while (firstBatchFetched)
        {
            foreach (var item in _underlying)
            {
                yield return item;
            }

            firstBatchFetched = await NextBatch();
        }
    }

    public IEnumerator<T> GetEnumerator()
    {
        return _underlying.GetEnumerator();
    }

    IAsyncEnumerator<T> IAsyncEnumerable<T>.GetAsyncEnumerator(CancellationToken cancellationToken)
    {
        return GetAsyncEnumerator(cancellationToken);
    }
}


public class PlayerCursorFactory : IPlayerCursorFactory
{
    private readonly ICacheProvider _cacheProvider;

    public PlayerCursorFactory(ICacheProvider cacheProvider)
    {
        _cacheProvider = cacheProvider;
    }

    public PlayerCursor<T> Create<T>() where T : notnull, PlayerEntity
    {
        return new PlayerCursor<T>(_cacheProvider);
    }
}
