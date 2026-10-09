using System.Collections.Concurrent;
using System.Data;
using System.Reflection;
using System.Runtime.ExceptionServices;

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
/// <b>interface</b> (e.g. <c>[Service(typeof(IShop))]</c>). At startup the Postgres configuration replaces that
/// registration with this proxy (same lifetime); a class registered as itself cannot be proxied and fails startup.
/// Calls must go through the interface: calls the implementation makes on <c>this</c> bypass the proxy. You never
/// construct this type yourself.
/// </para>
/// <para>
/// <b>Semantics.</b> If a transaction is already bound to the current async flow (<see cref="SqlAmbientTransaction.Current"/>,
/// e.g. an outer <c>[Transactional]</c> method or <see cref="ISqlTransactionProvider.InTransactionAsync{T}"/>), the call
/// simply joins it (no nested transaction or savepoint; its isolation level is ignored). Otherwise a connection is
/// opened from the registered <see cref="NpgsqlDataSource"/>, a transaction is begun and bound, the method runs, and
/// the transaction commits when the method's work has <b>completed</b> successfully, or rolls back if it throws:
/// </para>
/// <list type="bullet">
/// <item><description><see cref="Task"/>, <see cref="Task{TResult}"/>, <see cref="ValueTask"/>,
/// <see cref="ValueTask{TResult}"/>: when the returned task completes.</description></item>
/// <item><description><see cref="IAsyncEnumerable{T}"/>: the method runs when the enumeration starts; the
/// transaction commits when the enumeration completes and rolls back when it throws or is disposed early.</description></item>
/// <item><description>any other return type: when the method returns.</description></item>
/// </list>
/// <para>
/// Exceptions reach the caller unwrapped (not as <see cref="TargetInvocationException"/>). Methods without the
/// attribute are forwarded unchanged. Avoid running parallel commands inside the transaction: they share one connection.
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

    private static readonly MethodInfo TaskOfT = Generic(nameof(RunTaskCall));
    private static readonly MethodInfo ValueTaskOfT = Generic(nameof(RunValueTask));
    private static readonly MethodInfo AsyncEnumerableOfT = Generic(nameof(RunAsyncEnumerable));

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
        var method = targetMethod!;
        if (!TryGetMetadata(method, out var meta))
            return Call(method, args);

        var isolation = meta.Attribute.IsolationLevel;
        var returnType = method.ReturnType;

        if (returnType == typeof(Task))
            return RunTask<bool>(async () => { await ((Task)Call(method, args)!).ConfigureAwait(false); return true; }, isolation);

        if (returnType == typeof(ValueTask))
            return new ValueTask(RunTask<bool>(async () => { await ((ValueTask)Call(method, args)!).ConfigureAwait(false); return true; }, isolation));

        if (returnType.IsGenericType)
        {
            var definition = returnType.GetGenericTypeDefinition();
            var resultType = returnType.GenericTypeArguments[0];
            if (definition == typeof(Task<>))
                return TaskOfT.MakeGenericMethod(resultType).Invoke(this, [CallFactory(method, args), isolation]);
            if (definition == typeof(ValueTask<>))
                return ValueTaskOfT.MakeGenericMethod(resultType).Invoke(this, [CallFactory(method, args), isolation]);
            if (definition == typeof(IAsyncEnumerable<>))
                return AsyncEnumerableOfT.MakeGenericMethod(resultType).Invoke(this, [CallFactory(method, args), isolation]);
        }

        if (SqlAmbientTransaction.Current is not null)
            return Call(method, args);
        return SqlAmbientTransactionRunner.Run(DataSource.OpenConnection(), () => Call(method, args), isolation);
    }

    private static MethodInfo Generic(string name)
        => typeof(TransactionalDecorator<T>).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!;

    private Func<object?> CallFactory(MethodInfo method, object?[]? args) => () => Call(method, args);

    /// <summary>Invokes the inner method, rethrowing its own exception instead of a <see cref="TargetInvocationException"/>.</summary>
    private object? Call(MethodInfo method, object?[]? args)
    {
        try
        {
            return method.Invoke(Inner, args);
        }
        catch (TargetInvocationException ex) when (ex.InnerException is not null)
        {
            ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
            throw;
        }
    }

    private async Task<TResult> RunTask<TResult>(Func<Task<TResult>> work, IsolationLevel isolation)
    {
        if (SqlAmbientTransaction.Current is not null)
            return await work().ConfigureAwait(false);

        var conn = await DataSource.OpenConnectionAsync().ConfigureAwait(false);
        return await SqlAmbientTransactionRunner.RunAsync(conn, _ => work(), isolation, CancellationToken.None)
            .ConfigureAwait(false);
    }

    private Task<TResult> RunTaskCall<TResult>(Func<object?> call, IsolationLevel isolation)
        => RunTask(() => (Task<TResult>)call()!, isolation);

    private ValueTask<TResult> RunValueTask<TResult>(Func<object?> call, IsolationLevel isolation)
        => new(RunTask(() => ((ValueTask<TResult>)call()!).AsTask(), isolation));

    private IAsyncEnumerable<TItem> RunAsyncEnumerable<TItem>(Func<object?> call, IsolationLevel isolation)
        => new TransactionalAsyncEnumerable<TItem>(() => (IAsyncEnumerable<TItem>)call()!, DataSource, isolation);

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
}

/// <summary>
/// An <see cref="IAsyncEnumerable{T}"/> whose whole enumeration runs in one transaction: the transaction (and the
/// underlying sequence) starts on the first <c>MoveNextAsync</c>, commits when the sequence ends, and rolls back when
/// it throws or the enumerator is disposed before the end. Joins an ambient transaction active at that point instead.
/// </summary>
/// <typeparam name="TItem">Element type.</typeparam>
internal sealed class TransactionalAsyncEnumerable<TItem> : IAsyncEnumerable<TItem>
{
    private readonly Func<IAsyncEnumerable<TItem>> _source;
    private readonly NpgsqlDataSource _dataSource;
    private readonly IsolationLevel _isolation;

    /// <summary>Creates the sequence; nothing runs until it is enumerated.</summary>
    public TransactionalAsyncEnumerable(Func<IAsyncEnumerable<TItem>> source, NpgsqlDataSource dataSource, IsolationLevel isolation)
    {
        _source = source;
        _dataSource = dataSource;
        _isolation = isolation;
    }

    /// <inheritdoc/>
    public IAsyncEnumerator<TItem> GetAsyncEnumerator(CancellationToken cancellationToken = default)
        => new Enumerator(this, cancellationToken);

    private sealed class Enumerator : IAsyncEnumerator<TItem>
    {
        private readonly TransactionalAsyncEnumerable<TItem> _owner;
        private readonly CancellationToken _ct;
        private IAsyncEnumerator<TItem>? _inner;
        private NpgsqlConnection? _conn;
        private NpgsqlTransaction? _tx;
        private SqlAmbientTransaction.Scope? _scope;
        private bool _finished;

        public Enumerator(TransactionalAsyncEnumerable<TItem> owner, CancellationToken ct)
        {
            _owner = owner;
            _ct = ct;
        }

        public TItem Current => _inner is null ? default! : _inner.Current;

        public async ValueTask<bool> MoveNextAsync()
        {
            if (_finished)
                return false;

            if (_inner is null && SqlAmbientTransaction.Current is null)
            {
                _conn = await _owner._dataSource.OpenConnectionAsync(_ct).ConfigureAwait(false);
                _tx = await _conn.BeginTransactionAsync(_owner._isolation, _ct).ConfigureAwait(false);
            }

            using var binding = Bind();
            try
            {
                _inner ??= _owner._source().GetAsyncEnumerator(_ct);
                if (await _inner.MoveNextAsync().ConfigureAwait(false))
                    return true;
            }
            catch
            {
                await FinishAsync(commit: false).ConfigureAwait(false);
                throw;
            }

            await FinishAsync(commit: true).ConfigureAwait(false);
            return false;
        }

        public async ValueTask DisposeAsync()
        {
            // Disposed before the sequence ended: the method's work did not complete.
            if (!_finished)
            {
                using var binding = Bind();
                await FinishAsync(commit: false).ConfigureAwait(false);
            }
        }

        private IDisposable? Bind()
        {
            if (_conn is null || _tx is null)
                return null;
            var binding = SqlAmbientTransaction.Enter(_conn, _tx);
            _scope = SqlAmbientTransaction.Current;
            return binding;
        }

        private async Task FinishAsync(bool commit)
        {
            _finished = true;
            try
            {
                if (_inner is not null)
                    await _inner.DisposeAsync().ConfigureAwait(false);
                if (_tx is not null && commit)
                    await _tx.CommitAsync(CancellationToken.None).ConfigureAwait(false);
                else if (_tx is not null)
                    await RollbackAsync(_tx).ConfigureAwait(false);
            }
            finally
            {
                _scope?.MarkCompleted();
                if (_tx is not null)
                    await _tx.DisposeAsync().ConfigureAwait(false);
                if (_conn is not null)
                    await _conn.DisposeAsync().ConfigureAwait(false);
            }
        }

        private static async Task RollbackAsync(NpgsqlTransaction tx)
        {
            try { await tx.RollbackAsync(CancellationToken.None).ConfigureAwait(false); }
            catch (NpgsqlException) { /* broken connection: the server rolls back on close */ }
        }
    }
}
