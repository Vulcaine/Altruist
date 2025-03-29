namespace Altruist;

public interface IAltruistContext
{
    SupportedBackplane? Backplane { get; set; }
    ServerInfo ServerInfo { get; set; }
    HashSet<string> Endpoints { get; set; }
    bool EngineEnabled { get; set; }
    void AddEndpoint(string endpoint);
}