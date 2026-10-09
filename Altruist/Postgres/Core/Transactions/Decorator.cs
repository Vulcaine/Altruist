using System.Collections.Concurrent;
using System.Reflection;

using Npgsql;

namespace Altruist.Persistence;

// Not sealed: DispatchProxy.Create rejects a sealed proxy base type ("cannot be sealed").
/// <summary>
/// <see cref="DispatchProxy"/> that gives methods marked with <see cref="TransactionalAttribute"/> an ambient SQL
/// transaction: the method body and every vault / <see cref="ISqlDatabaseProvider"/> call awaited inside it run on
/// one connection and commit together, or roll back together when the method throws.
/// </summary>
/// <remarks>
/// <para>
/// <b>Opting in.</b> Put <c>[Transactional]</c> (optionally with an <see cref="System.Data.IsolationLevel"/>, default
/// <c>ReadCommitted</c>) on a public method of a service <i>implementation</i> that is registered in DI under an
/// <b>interface</b> with an implementation type (e.g. <c>[Service(typeof(IShop))]</c>). At startup the Postgres
/// configuration replaces that registration with this proxy (same lifetime). Calls must go through the interface:
/// calls the implementation makes on <c>this</c> bypass the proxy. You never construct this type yourself.
/// </para>
/// <para>
/// <b>Semantics.</b> If a transaction is already bound to the current async flow (<see cref="SqlAmbientTransaction.Current"/>,
/// e.g. an outer <c>[Transactional]</c> method or <see cref="ISqlTransactionProvider.InTransactionAsync{T}"/>), the call
/// simply joins it (no nested transaction or savepoint; its isolation level is ignored). Otherwise a connection is
/// opened from the registered <see cref="NpgsqlDataSource"/>, a transaction is begun and bound, the method runs, and
/// the transaction commits when the returned <see cref="Task"/> completes successfully, or rolls back if it throws.
/// Methods returning <see cref="Task"/> / <see cref="Task{TResult}"/> are awaited; any other return type (including
/// <c>ValueTask</c> and <c>IAsyncEnumerable</c>) is treated as synchronous, i.e. committed as soon as the method
/// returns. Methods without the attribute are forwarded unchanged. Avoid running parallel commands inside the
/// transaction: they share one connection.
/// </para>
/// </remarks>
/// <typeparam name="T">The proxied service interface.</typeparam>
/// <example>
/// <code>
/// public interface ITransferService { Task MoveAsync(string from, string to, int amount); }
///
/// [Service(typeof(ITransferService), ServiceLifetime.Scoped)]
/// public sealed class TransferService(IVault&lt;WalletVault&gt; wallets) : ITransferService
/// {
///     [Transactional]
///     public async Task MoveAsync(string from, string to, int amount)
///     {
///         var a = await wallets.Where(w =&gt; w.StorageId == from).FirstAsync();
///         var b = await wallets.Where(w =&gt; w.StorageId == to).FirstAsync();
///         a.Balance -= amount; b.Balance += amount;
///         await wallets.SaveBatchAsync(new[] { a, b }); // both rows or neither
///     }
/// }
/// </code>
/// </example>
public class TransactionalDecorator<T> : DispatchProxy
{
    // The proxy is invoked with the interface's MethodInfo, while TransactionalRegistry holds the
    // implementation's [Transactional] methods; map one to the other once per method.
    private static readonly ConcurrentDictionary<(Type Impl, MethodInfo Method), MethodInfo?> ImplementationMethods = new();

    /// <summary>The real service instance calls are forwarded to. Set once by the registration factory.</summary>
    public T Inner = default!;
    /// <summary>Source of the connection a new transaction is opened on. Set once by the registration factory.</summary>
    public NpgsqlDataSource DataSource = default!;

    /// <summary>
    /// Proxy entry point: looks up <see cref="TransactionalAttribute"/> metadata for the called method (or the
    /// implementation method mapped from the interface method) and either forwards the call directly or runs it in a
    /// transaction as described on the class.
    /// </summary>
    /// <param name="targetMethod">The interface method being invoked.</param>
    /// <param name="args">Call arguments.</param>
    /// <returns>The method's return value (for async methods a task that completes after commit).</returns>
    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        // Fast O(1) lookup instead of GetCustomAttribute on every call
        if (!TryGetMetadata(targetMethod!, out var meta))
            return targetMethod!.Invoke(Inner, args);

        // Decide sync/async
        var returnType = targetMethod!.ReturnType;
        if (typeof(Task).IsAssignableFrom(returnType))
        {
            return InvokeAsync(targetMethod, args, meta.Attribute, returnType);
        }

        return InvokeSync(targetMethod, args, meta.Attribute);
    }

    private bool TryGetMetadata(MethodInfo method, out TransactionalMetadata meta)
    {
        if (TransactionalRegistry.TryGet(method, out meta))
            return true;
        if (Inner is null || method.DeclaringType is not { IsInterface: true })
            return false;

        var impl = ImplementationMethods.GetOrAdd((Inner.GetType(), method), static key =>
        {
            var lookup = key.Method.IsGenericMethod ? key.Method.GetGenericMethodDefinition() : key.Method;
            var map = key.Impl.GetInterfaceMap(lookup.DeclaringType!);
            var index = Array.IndexOf(map.InterfaceMethods, lookup);
            return index >= 0 ? map.TargetMethods[index] : null;
        });
        return impl is not null && TransactionalRegistry.TryGet(impl, out meta);
    }

    private object? InvokeSync(MethodInfo method, object?[]? args, TransactionalAttribute attr)
    {
        // Join an outer transaction instead of opening a second, independent one.
        if (SqlAmbientTransaction.Current is not null)
            return method.Invoke(Inner, args);

        using var conn = DataSource.OpenConnection();
        using var tx = conn.BeginTransaction(attr.IsolationLevel);
        using var bound = SqlAmbientTransaction.Enter(conn, tx);

        try
        {
            var result = method.Invoke(Inner, args);
            tx.Commit();
            return result;
        }
        catch
        {
            tx.Rollback();
            throw;
        }
    }

    private object InvokeAsync(MethodInfo method, object?[]? args, TransactionalAttribute attr, Type returnType)
    {
        // handle Task / Task<T>
        if (returnType == typeof(Task))
            return InvokeAsyncNonGeneric(method, args, attr);

        var taskResultType = returnType.GenericTypeArguments[0];
        var invoker = typeof(TransactionalDecorator<T>)
            .GetMethod(nameof(InvokeAsyncGeneric), BindingFlags.Instance | BindingFlags.NonPublic)!
            .MakeGenericMethod(taskResultType);

        return invoker.Invoke(this, [method, args, attr])!;
    }

    private async Task InvokeAsyncNonGeneric(MethodInfo method, object?[]? args, TransactionalAttribute attr)
    {
        if (SqlAmbientTransaction.Current is not null)
        {
            await ((Task)method.Invoke(Inner, args)!).ConfigureAwait(false);
            return;
        }

        await using var conn = await DataSource.OpenConnectionAsync().ConfigureAwait(false);
        await using var tx = await conn.BeginTransactionAsync(attr.IsolationLevel).ConfigureAwait(false);
        // Bind the transaction to this async flow so the vault/provider calls inside the
        // method actually run on it (previously they used their own connections: no atomicity).
        using var bound = SqlAmbientTransaction.Enter(conn, tx);

        try
        {
            var task = (Task)method.Invoke(Inner, args)!;
            await task.ConfigureAwait(false);
            await tx.CommitAsync().ConfigureAwait(false);
        }
        catch
        {
            await tx.RollbackAsync().ConfigureAwait(false);
            throw;
        }
    }

    private async Task<TResult> InvokeAsyncGeneric<TResult>(
        MethodInfo method,
        object?[]? args,
        TransactionalAttribute attr)
    {
        if (SqlAmbientTransaction.Current is not null)
            return await ((Task<TResult>)method.Invoke(Inner, args)!).ConfigureAwait(false);

        await using var conn = await DataSource.OpenConnectionAsync().ConfigureAwait(false);
        await using var tx = await conn.BeginTransactionAsync(attr.IsolationLevel).ConfigureAwait(false);
        // Bind the transaction to this async flow so the vault/provider calls inside the
        // method actually run on it (previously they used their own connections: no atomicity).
        using var bound = SqlAmbientTransaction.Enter(conn, tx);

        try
        {
            var task = (Task<TResult>)method.Invoke(Inner, args)!;
            var result = await task.ConfigureAwait(false);
            await tx.CommitAsync().ConfigureAwait(false);
            return result;
        }
        catch
        {
            await tx.RollbackAsync().ConfigureAwait(false);
            throw;
        }
    }
}
