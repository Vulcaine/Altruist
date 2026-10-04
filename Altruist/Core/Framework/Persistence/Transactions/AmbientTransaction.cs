/*
Copyright 2025 Aron Gere

Licensed under the Apache License, Version 2.0 (the "License");
you may not use this file except in compliance with the License.
You may obtain a copy of the License at

    http://www.apache.org/licenses/LICENSE-2.0
*/

using System.Data;
using System.Data.Common;

namespace Altruist.Persistence;

/// <summary>
/// The SQL transaction bound to the current async flow. While one is active, every
/// <see cref="GeneralSqlDatabaseProvider"/> operation (vault queries, saves, raw SQL) runs on its
/// connection inside its transaction instead of opening a fresh pooled connection.
/// </summary>
public static class SqlAmbientTransaction
{
    private static readonly AsyncLocal<Scope?> _current = new();

    /// <summary>The active transaction for this async flow, or null.</summary>
    public static Scope? Current => _current.Value is { IsCompleted: false } s ? s : null;

    /// <summary>
    /// Binds an open connection + transaction to the current async flow. Disposing the returned
    /// handle restores the previous binding; it does not commit, roll back or close anything.
    /// </summary>
    public static IDisposable Enter(DbConnection connection, DbTransaction transaction)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(transaction);
        var previous = _current.Value;
        _current.Value = new Scope(connection, transaction);
        return new Restore(previous);
    }

    public sealed class Scope
    {
        internal Scope(DbConnection connection, DbTransaction transaction)
        {
            Connection = connection;
            Transaction = transaction;
        }

        public DbConnection Connection { get; }
        public DbTransaction Transaction { get; }

        /// <summary>Set once the transaction committed or rolled back; the scope is then ignored.</summary>
        public bool IsCompleted { get; internal set; }

        /// <summary>
        /// Serializes commands on the shared connection (ADO.NET connections are not thread-safe;
        /// this keeps accidental parallel awaits inside a transaction from corrupting the protocol).
        /// </summary>
        internal SemaphoreSlim Gate { get; } = new(1, 1);
    }

    private sealed class Restore : IDisposable
    {
        private readonly Scope? _previous;
        private bool _done;

        public Restore(Scope? previous) => _previous = previous;

        public void Dispose()
        {
            if (_done)
                return;
            _done = true;
            _current.Value = _previous;
        }
    }
}

/// <summary>Providers that can run a block of operations atomically.</summary>
public interface ISqlTransactionProvider
{
    /// <summary>
    /// Runs <paramref name="work"/> inside one transaction: every operation of this provider (and
    /// of vaults using it) awaited within it uses the same connection. Commits when the work
    /// completes, rolls back when it throws. Nested calls join the outer transaction.
    /// </summary>
    /// <remarks>
    /// A callback (instead of a returned scope object) is required: an AsyncLocal assigned inside
    /// an awaited async method does not flow back to its caller, so a scope "entered" by a
    /// Begin...Async method would never be visible to the code after the await.
    /// </remarks>
    Task<T> InTransactionAsync<T>(
        Func<CancellationToken, Task<T>> work,
        IsolationLevel isolation = IsolationLevel.ReadCommitted,
        CancellationToken ct = default);
}

public static class SqlAmbientTransactionRunner
{
    /// <summary>Binds a transaction on <paramref name="openConnection"/> (which it then owns) around <paramref name="work"/>.</summary>
    public static async Task<T> RunAsync<T>(
        DbConnection openConnection,
        Func<CancellationToken, Task<T>> work,
        IsolationLevel isolation,
        CancellationToken ct)
    {
        await using var conn = openConnection;
        await using var tx = await conn.BeginTransactionAsync(isolation, ct).ConfigureAwait(false);
        var binding = SqlAmbientTransaction.Enter(conn, tx);
        var scope = SqlAmbientTransaction.Current!;
        try
        {
            var result = await work(ct).ConfigureAwait(false);
            await tx.CommitAsync(ct).ConfigureAwait(false);
            return result;
        }
        catch
        {
            try { await tx.RollbackAsync(CancellationToken.None).ConfigureAwait(false); }
            catch { /* broken connection: the server rolls back on close */ }
            throw;
        }
        finally
        {
            scope.IsCompleted = true;
            binding.Dispose();
        }
    }
}
