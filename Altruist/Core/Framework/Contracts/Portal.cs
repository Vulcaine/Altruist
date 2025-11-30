namespace Altruist;

public interface IPortal
{
    /// <summary>
    /// Called when a new connection is accepted and associated with clientId.
    /// Default: no-op.
    /// </summary>
    Task OnConnectedAsync(string clientId, ConnectionManager connection) => Task.CompletedTask;

    /// <summary>
    /// Called when a connection is closed.
    /// Default: no-op.
    /// </summary>
    Task OnDisconnectedAsync(string clientId, Exception? exception) => Task.CompletedTask;
}

public abstract class Portal : IPortal
{
    public virtual Task OnConnectedAsync(string clientId, ConnectionManager connection) => Task.CompletedTask;

    public virtual Task OnDisconnectedAsync(string clientId, Exception? exception) => Task.CompletedTask;
}
