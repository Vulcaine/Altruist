namespace Altruist;

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