namespace Altruist;

public interface IConnectable
{
    bool IsConnected { get; }
    event Action? OnConnected;
    event Action<Exception> OnFailed;
}

public interface IRelayService
{
    string RelayEvent { get; }
    Task Relay(IPacket data);
    Task ConnectAsync();
}

public abstract class AbstractRelayService : IRelayService
{
    public abstract string RelayEvent { get; }

    public abstract Task ConnectAsync();
    public abstract Task Relay(IPacket data);
}