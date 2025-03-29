using Altruist.Contracts;

namespace Altruist;

public interface IAltruistContext
{
    ITransportServiceToken TransportToken { get; set; }
    IDatabaseServiceToken? DatabaseToken { get; set; }
    ICacheServiceToken? CacheToken { get; set; }
    ServerInfo ServerInfo { get; set; }
    HashSet<string> Endpoints { get; set; }
    bool EngineEnabled { get; set; }
    void AddEndpoint(string endpoint);
    void Validate();
}