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

    /// <summary>An open connection and transaction bound to an async flow by <see cref="Enter"/>.</summary>
    public sealed class Scope
    {
        internal Scope(DbConnection connection, DbTransaction transaction)
        {
            Connection = connection;
            Transaction = transaction;
        }

        /// <summary>The open connection the transaction runs on; providers execute their commands on it.</summary>
        public DbConnection Connection { get; }
        /// <summary>The active transaction to attach commands to.</summary>
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
/// <remarks>
/// Implemented by <see cref="GeneralSqlDatabaseProvider"/>; obtain it by casting the injected
/// <see cref="ISqlDatabaseProvider"/> (<c>db as ISqlTransactionProvider</c>). Use it for an atomic block
/// inside a method; to make a whole DI service method atomic declaratively, annotate it with
/// <see cref="TransactionalAttribute"/> instead.
/// </remarks>
/// <example>
/// <code>
/// var tx = (ISqlTransactionProvider)db;
/// var saved = await tx.InTransactionAsync(async ct =&gt;
/// {
///     await vault.SaveAsync(order, ct: ct);
///     await vault.SaveAsync(invoice, ct: ct);
///     return true;
/// });
/// </code>
/// </example>
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
    /// <typeparam name="T">Result type of <paramref name="work"/>.</typeparam>
    /// <param name="work">The operations to run; receives <paramref name="ct"/>. Must be awaited sequentially (commands on the shared connection are serialized).</param>
    /// <param name="isolation">Isolation level for a new transaction; ignored when joining an outer one.</param>
    /// <param name="ct">Cancellation token passed to the work and to begin/commit.</param>
    /// <returns>The value returned by <paramref name="work"/>, after commit.</returns>
    Task<T> InTransactionAsync<T>(
        Func<CancellationToken, Task<T>> work,
        IsolationLevel isolation = IsolationLevel.ReadCommitted,
        CancellationToken ct = default);
}

/// <summary>
/// Shared helper for <see cref="ISqlTransactionProvider"/> implementations: begins a transaction on an
/// already opened connection, binds it with <see cref="SqlAmbientTransaction.Enter"/>, runs the work,
/// then commits or rolls back and disposes the connection.
/// </summary>
/// <remarks>
/// Does not check for an existing ambient transaction; callers that support nesting must test
/// <see cref="SqlAmbientTransaction.Current"/> first and run the work directly when one is active
/// (as <see cref="GeneralSqlDatabaseProvider.InTransactionAsync{T}"/> does).
/// Application code should call <see cref="ISqlTransactionProvider.InTransactionAsync{T}"/> instead.
/// </remarks>
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
