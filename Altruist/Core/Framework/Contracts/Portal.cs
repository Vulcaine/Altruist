namespace Altruist;

public interface IPortal
{
    public string Route { get; set; }
}

public abstract class Portal : IPortal
{
    public string Route { get; set; } = "";
}


public interface OnConnectedAsync
{
    public Task OnConnectedAsync(string clientId, ConnectionManager connectionManager, AltruistConnection connection);
}


public interface OnDisconnectedAsync
{
    public Task OnDisconnectedAsync(string clientId, Exception? exception);
}
