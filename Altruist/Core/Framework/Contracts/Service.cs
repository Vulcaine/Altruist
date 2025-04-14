namespace Altruist;

public interface IConnectable
{
    bool IsConnected { get; }
    event Action? OnConnected;
    event Action<Exception> OnFailed;
    event Action<Exception> OnRetryExhausted;

    void RaiseConnectedEvent();
    void RaiseFailedEvent(Exception ex);
    void RaiseOnRetryExhaustedEvent(Exception ex);
    Task ConnectAsync(int maxRetries = 30, int delayMilliseconds = 2000);
}

public interface IRelayService : IConnectable
{
    string RelayEvent { get; }
    Task Relay(IPacket data);
}

public abstract class AbstractRelayService : IRelayService
{
    public abstract string RelayEvent { get; }

    public bool IsConnected => throw new NotImplementedException();

    public event Action? OnConnected;
    public event Action<Exception> OnRetryExhausted = _ => { };
    public event Action<Exception> OnFailed = _ => { };

    public abstract Task ConnectAsync(int maxRetries = 30, int delayMilliseconds = 2000);
    public abstract Task Relay(IPacket data);

    public void RaiseConnectedEvent()
    {
        OnConnected?.Invoke();
    }

    public void RaiseFailedEvent(Exception ex)
    {
        OnFailed?.Invoke(ex);
    }

    public void RaiseOnRetryExhaustedEvent(Exception ex)
    {
        OnRetryExhausted?.Invoke(ex);
    }
}