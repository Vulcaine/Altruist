using Altruist.Contracts;

namespace Altruist;

public interface IAltruistContext
{
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