namespace Altruist;

public interface IPortal
{
    public string Route { get; set; }
    /// <summary>
    /// Called when a new connection is accepted and associated with clientId.
    /// Default: no-op.
    /// </summary>
    Task OnConnectedAsync(string clientId, ConnectionManager connectionManager, AltruistConnection connection) => Task.CompletedTask;

    /// <summary>
    /// Called when a connection is closed.
    /// Default: no-op.
    /// </summary>
    Task OnDisconnectedAsync(string clientId, Exception? exception) => Task.CompletedTask;
}

public abstract class Portal : IPortal
{
    public string Route { get; set; } = "";

    public virtual Task OnConnectedAsync(string clientId, ConnectionManager connectionManager, AltruistConnection connection) => Task.CompletedTask;

    public virtual Task OnDisconnectedAsync(string clientId, Exception? exception) => Task.CompletedTask;
}
