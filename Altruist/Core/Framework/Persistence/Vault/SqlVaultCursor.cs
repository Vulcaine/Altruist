/*
Copyright 2025 Aron Gere
Licensed under the Apache License, Version 2.0
*/

namespace Altruist.Persistence;

/// <summary>
/// Batched cursor returned by <see cref="SqlVault{TVaultModel}.ToCursorAsync"/>: each <see cref="NextBatch"/> runs one
/// paged query for the next <c>batchSize</c> rows.
/// </summary>
/// <typeparam name="T">The vault model type.</typeparam>
internal sealed class SqlVaultCursor<T> : ICursor<T> where T : class, IVaultModel
{
    private readonly Func<int, int, CancellationToken, Task<List<T>>> _fetch;
    private readonly int _batchSize;
    private readonly CancellationToken _ct;
    private int _offset;
    private bool _exhausted;

    /// <summary>Creates the cursor.</summary>
    /// <param name="fetch">Reads <c>count</c> rows starting at <c>offset</c> of the query's window.</param>
    /// <param name="batchSize">Rows per batch (positive).</param>
    /// <param name="ct">Token observed by every batch.</param>
    public SqlVaultCursor(Func<int, int, CancellationToken, Task<List<T>>> fetch, int batchSize, CancellationToken ct)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(batchSize);
        _fetch = fetch;
        _batchSize = batchSize;
        _ct = ct;
    }

    /// <summary>False once a batch came back shorter than the batch size.</summary>
    public bool HasNext => !_exhausted;

    /// <summary>Always <c>-1</c>: the total is unknown without a separate <c>COUNT</c>.</summary>
    public int Count => -1;

    /// <summary>Reads the next batch; empty once the cursor is exhausted.</summary>
    /// <returns>The rows of the batch.</returns>
    public async Task<IEnumerable<T>> NextBatch()
    {
        if (_exhausted)
            return Array.Empty<T>();

        var rows = await _fetch(_offset, _batchSize, _ct).ConfigureAwait(false);
        _offset += rows.Count;
        if (rows.Count < _batchSize)
            _exhausted = true;
        return rows;
    }

    /// <summary>Enumerates the remaining rows, blocking on each batch; prefer <see cref="NextBatch"/> in async code.</summary>
    /// <returns>The enumerator.</returns>
    public IEnumerator<T> GetEnumerator()
    {
        while (HasNext)
        {
            foreach (var row in NextBatch().GetAwaiter().GetResult())
                yield return row;
        }
    }
}
