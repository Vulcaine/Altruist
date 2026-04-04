using System.Data;

namespace Altruist.Persistence;

[AttributeUsage(AttributeTargets.Method)]
public sealed class TransactionalAttribute : Attribute
{
    public IsolationLevel IsolationLevel { get; }
    public TransactionalAttribute(IsolationLevel isolation = IsolationLevel.ReadCommitted)
        => IsolationLevel = isolation;
}
