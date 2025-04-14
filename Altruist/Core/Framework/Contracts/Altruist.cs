using Altruist.Contracts;

namespace Altruist;

public interface IAppStatus
{
    ReadyState Status { get; }
    void SignalState(ReadyState state);
    Task StartupAsync(AppManager manager);
}


public interface IAltruistContext
{
    IAppStatus AppStatus { get; set; }
    ITransportServiceToken TransportToken { get; set; }
    List<IDatabaseServiceToken> DatabaseTokens { get; set; }
    ICacheServiceToken? CacheToken { get; set; }
    ServerInfo ServerInfo { get; set; }
    HashSet<string> Endpoints { get; set; }
    public string ProcessId { get; }
    bool EngineEnabled { get; set; }
    void AddEndpoint(string endpoint);
    void Validate();
}