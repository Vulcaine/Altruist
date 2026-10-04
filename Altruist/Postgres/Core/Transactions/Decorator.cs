using System.Collections.Concurrent;
using System.Reflection;

using Npgsql;

namespace Altruist.Persistence;

// Not sealed: DispatchProxy.Create rejects a sealed proxy base type ("cannot be sealed").
public class TransactionalDecorator<T> : DispatchProxy
{
    // The proxy is invoked with the interface's MethodInfo, while TransactionalRegistry holds the
    // implementation's [Transactional] methods; map one to the other once per method.
    private static readonly ConcurrentDictionary<(Type Impl, MethodInfo Method), MethodInfo?> ImplementationMethods = new();

    public T Inner = default!;
    public NpgsqlDataSource DataSource = default!;

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
