using System.Data;

namespace Altruist.Persistence;

/// <summary>
/// Marks a service method so that, when called through its DI-resolved service interface, it runs inside one SQL
/// transaction: commits when the method's work has completed, rolls back when it throws.
/// </summary>
/// <remarks>
/// <para>
/// Applied by the Postgres configuration at startup: every non-keyed DI registration whose service type is an
/// interface implemented by a class with <c>[Transactional]</c> methods returns a <c>DispatchProxy</c>-based decorator
/// (<c>TransactionalDecorator&lt;T&gt;</c>, keeping the original lifetime) around the real instance, whether the
/// service was registered by type, instance or factory (including <c>[Service(typeof(IMyService))]</c>). Only calls made
/// through the interface go through the proxy: resolving the class itself, or a call from inside the class to its own
/// method (<c>this.Foo()</c>), is not transactional.
/// </para>
/// <para>
/// While the transaction is active it is bound to the async flow via <see cref="SqlAmbientTransaction"/>,
/// so vault queries, saves and raw SQL awaited inside the method share its connection. If a transaction is
/// already active (nested <c>[Transactional]</c> call or <see cref="ISqlTransactionProvider.InTransactionAsync{T}"/>),
/// the method joins it and the outer isolation level applies. Supported return types: synchronous methods (commit on
/// return), <see cref="Task"/>, <see cref="Task{TResult}"/>, <see cref="ValueTask"/>, <see cref="ValueTask{TResult}"/>
/// (commit when the task completes) and <see cref="IAsyncEnumerable{T}"/> (the transaction spans the enumeration:
/// commit at its end, rollback when it throws or is abandoned). Exceptions reach the caller unwrapped.
/// </para>
/// <para>
/// Use this to make a whole service operation atomic declaratively. For an atomic block inside a method,
/// or code that is not a DI service, call <see cref="ISqlTransactionProvider.InTransactionAsync{T}"/> instead.
/// </para>
/// </remarks>
/// <example>
/// <code>
/// public interface IWalletService { Task TransferAsync(string from, string to, long amount); }
/// [Service(typeof(IWalletService))]
/// public sealed class WalletService : IWalletService
/// {
///     [Transactional(IsolationLevel.Serializable)]
///     public async Task TransferAsync(string from, string to, long amount)
///     {
///         // both saves commit together, or neither does
///         await _accounts.SaveAsync(debit);
///         await _accounts.SaveAsync(credit);
///     }
/// }
/// </code>
/// </example>
[AttributeUsage(AttributeTargets.Method)]
public sealed class TransactionalAttribute : Attribute
{
    /// <summary>Isolation level used when this method opens a new transaction (ignored when it joins an outer one).</summary>
    public IsolationLevel IsolationLevel { get; }
    /// <summary>Creates the attribute.</summary>
    /// <param name="isolation">Isolation level of the transaction; defaults to <see cref="IsolationLevel.ReadCommitted"/>.</param>
    public TransactionalAttribute(IsolationLevel isolation = IsolationLevel.ReadCommitted)
        => IsolationLevel = isolation;
}
