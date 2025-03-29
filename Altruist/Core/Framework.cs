
using Altruist.Contracts;

namespace Altruist
{
    public class AltruistServerContext : IAltruistContext
    {
        public ServerInfo ServerInfo { get; set; } = new ServerInfo("Altruist Server", "ws", "localhost", 3001);
        public SupportedBackplane? Backplane { get; set; }

        public HashSet<string> Endpoints { get; set; } = new HashSet<string>();

        public bool EngineEnabled { get; set; }

        public ITransportServiceToken TransportToken { get; set; }

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
                lines.Add($"💻 {serverString}{endpoint}");
            }

            if (!string.IsNullOrEmpty(Backplane?.ToString()))
            {
                lines.Add(Backplane.ToString());
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
