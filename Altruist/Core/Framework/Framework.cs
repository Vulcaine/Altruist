
using Altruist.Contracts;

namespace Altruist
{
    public class AltruistServerContext : IAltruistContext
    {
        public ServerInfo ServerInfo { get; set; } = new ServerInfo("Altruist Server", "ws", "localhost", 3001);

        public HashSet<string> Endpoints { get; set; } = new HashSet<string>();

        public bool EngineEnabled { get; set; }

        public string ProcessId { get; } = $"{Environment.MachineName}-{Environment.ProcessId}-${Guid.NewGuid()}";

        public ITransportServiceToken TransportToken { get; set; }
        public List<IDatabaseServiceToken> DatabaseTokens { get; set; } = new List<IDatabaseServiceToken>();
        public ICacheServiceToken? CacheToken { get; set; }
        public IServerStatus AppStatus { get; set; }

        public void AddEndpoint(string endpoint) => Endpoints.Add(endpoint);

        public void Validate()
        {
            if (Endpoints.Count == 0)
            {
                throw new ArgumentException("No endpoints to listen to. Setup a transport with .UseTransport");
            }

            if (TransportToken == null)
            {
                throw new ArgumentException("No transport setup. Setup a transport with .UseTransport");
            }
        }

        public override string ToString()
        {
            var lines = new List<string>();
            var serverString = $"{ServerInfo.Protocol}://{ServerInfo.Host}:{ServerInfo.Port}";

            foreach (var endpoint in Endpoints)
            {
                lines.Add($"💻 Address: {serverString}{endpoint}");
            }

            if (!string.IsNullOrEmpty(TransportToken.Description))
            {
                lines.Add(TransportToken.Description);
            }

            if (!string.IsNullOrEmpty(CacheToken?.Description))
            {
                lines.Add(CacheToken.Description);
            }

            foreach (var databaseToken in DatabaseTokens)
            {
                if (!string.IsNullOrEmpty(databaseToken.Description))
                {
                    lines.Add(databaseToken.Description);
                }
            }

            return string.Join(Environment.NewLine, lines);
        }

    }

    [AttributeUsage(AttributeTargets.Method, Inherited = false, AllowMultiple = true)]
    public sealed class GateAttribute : Attribute
    {
        public string Event { get; }

        public GateAttribute(string eventName)
        {
            Event = eventName;
        }
    }

}
