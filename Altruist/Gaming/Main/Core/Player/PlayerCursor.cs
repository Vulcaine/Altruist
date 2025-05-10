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
    private readonly ICacheProvider _cacheProvider;

    private ICursor<T> _underlying;

    public bool HasNext => _underlying.HasNext;

    public PlayerCursor(ICacheProvider cacheProvider)
    {
        _cacheProvider = cacheProvider;
        _underlying = CreateCursorAsync().GetAwaiter().GetResult();
    }

    private async Task<ICursor<T>> CreateCursorAsync()
    {
        _underlying = await _cacheProvider.GetAllAsync<T>();
        return _underlying;
    }

    public async Task<IEnumerable<T>> NextBatch()
    {
        return await _underlying.NextBatch();
    }

    public async IAsyncEnumerator<T> GetAsyncEnumerator(CancellationToken cancellationToken = default)
    {
        var cursor = await CreateCursorAsync();

        while (true)
        {
            if (!HasNext)
                yield break;

            var batch = await cursor.NextBatch();
            if (batch.Count() == 0)
                yield break;

            foreach (var item in batch)
            {
                yield return item;
            }
        }
    }

    public IEnumerator<T> GetEnumerator()
    {
        var cursor = CreateCursorAsync().GetAwaiter().GetResult();

        while (true)
        {
            if (!HasNext)
                yield break;

            var batch = cursor.NextBatch().GetAwaiter().GetResult();
            if (batch.Count() == 0)
                yield break;

            foreach (var item in batch)
            {
                yield return item;
            }
        }
    }

}

[Service(typeof(IPlayerCursorFactory))]
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
