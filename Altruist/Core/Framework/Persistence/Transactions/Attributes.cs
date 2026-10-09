using System.Data;

namespace Altruist.Persistence;

/// <summary>
/// Marks a service method so that, when called through its DI-resolved service, it runs inside one SQL
/// transaction: commits when the method returns (or its task completes), rolls back when it throws.
/// </summary>
/// <remarks>
/// <para>
/// Applied by the Postgres configuration at startup: <see cref="TransactionalRegistry.WarmUp"/> scans the
/// assemblies, and every DI registration whose implementation type has a <c>[Transactional]</c> method is
/// replaced by a <c>DispatchProxy</c>-based decorator (<c>TransactionalDecorator&lt;T&gt;</c>, keeping the
/// original lifetime). Only registrations with an implementation type are wrapped (not factory or instance
/// registrations), and only calls made through the resolved service go through the proxy: a call from
/// inside the class to its own method (<c>this.Foo()</c>) is not transactional.
/// </para>
/// <para>
/// While the transaction is active it is bound to the async flow via <see cref="SqlAmbientTransaction"/>,
/// so vault queries, saves and raw SQL awaited inside the method share its connection. If a transaction is
/// already active (nested <c>[Transactional]</c> call or <see cref="ISqlTransactionProvider.InTransactionAsync{T}"/>),
/// the method joins it and the outer isolation level applies.
/// Supports synchronous methods, <see cref="Task"/> and <see cref="Task{TResult}"/> return types.
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
