namespace Altruist;

public class InterceptContext
{
    public string EventName { get; }

    public InterceptContext(string eventName)
    {
        EventName = eventName;
    }
}


public interface IInterceptor
{
    Task Intercept(InterceptContext context, IPacket eventData);
}


public class RelayInterceptor : IInterceptor
{
    private readonly IRelayService _relayService;

    public RelayInterceptor(IRelayService relayService)
    {
        _relayService = relayService;
    }

    public async Task Intercept(InterceptContext context, IPacket eventData)
    {
        if (context.EventName == _relayService.RelayEvent)
        {
            await _relayService.Relay(eventData);
        }
    }
}
