using System.Reflection;

using Npgsql;

namespace Altruist.Persistence;

public sealed class TransactionalDecorator<T> : DispatchProxy
{
    public T Inner = default!;
    public NpgsqlDataSource DataSource = default!;

    protected override object? Invoke(MethodInfo targetMethod, object?[]? args)
    {
        // Fast O(1) lookup instead of GetCustomAttribute on every call
        if (!TransactionalRegistry.TryGet(targetMethod, out var meta))
            return targetMethod.Invoke(Inner, args);

        // Decide sync/async
        var returnType = targetMethod.ReturnType;
        if (typeof(Task).IsAssignableFrom(returnType))
        {
            return InvokeAsync(targetMethod, args, meta.Attribute, returnType);
        }

        return InvokeSync(targetMethod, args, meta.Attribute);
    }

    private object? InvokeSync(MethodInfo method, object?[]? args, TransactionalAttribute attr)
    {
        using var conn = DataSource.OpenConnection();
        using var tx = conn.BeginTransaction(attr.IsolationLevel);

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

        return invoker.Invoke(this, new object?[] { method, args, attr })!;
    }

    private async Task InvokeAsyncNonGeneric(MethodInfo method, object?[]? args, TransactionalAttribute attr)
    {
        await using var conn = await DataSource.OpenConnectionAsync().ConfigureAwait(false);
        await using var tx = await conn.BeginTransactionAsync(attr.IsolationLevel).ConfigureAwait(false);

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
        await using var conn = await DataSource.OpenConnectionAsync().ConfigureAwait(false);
        await using var tx = await conn.BeginTransactionAsync(attr.IsolationLevel).ConfigureAwait(false);

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
