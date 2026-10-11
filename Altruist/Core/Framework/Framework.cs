/*
Copyright 2025 Aron Gere

Licensed under the Apache License, Version 2.0 (the "License");
you may not use this file except in compliance with the License.
You may obtain a copy of the License at

    http://www.apache.org/licenses/LICENSE-2.0

Unless required by applicable law or agreed to in writing, software
distributed under the License is distributed on an "AS IS" BASIS,
WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
See the License for the specific language governing permissions and
limitations under the License.
*/

using Altruist.Contracts;

namespace Altruist
{
    /// <summary>
    /// Default <see cref="IAltruistContext"/>: process-wide server facts (address, portal endpoints, active transport /
    /// database / cache tokens) used for the startup banner and diagnostics. Singleton, registered by <see cref="ServiceAttribute"/>;
    /// inject <see cref="IAltruistContext"/>.
    /// </summary>
    [Service(typeof(IAltruistContext))]
    public class AltruistServerContext : IAltruistContext
    {
        /// <summary>Public address; replaced with the configured HTTP host/port when the host starts.</summary>
        public ServerInfo ServerInfo { get; set; } = new ServerInfo("Altruist Server", "ws", "localhost", 3001);

        /// <summary>Portal endpoint paths, filled during portal warmup.</summary>
        public HashSet<string> Endpoints { get; set; } = new HashSet<string>();

        /// <summary>True when an engine configuration (<see cref="EngineConfigOptions"/>, i.e. <c>altruist:game:engine</c>) was present at construction.</summary>
        public bool EngineEnabled { get; set; }

        /// <summary>Unique id of this process instance (<c>machine-pid-guid</c>).</summary>
        public string ProcessId { get; } = $"{Environment.MachineName}-{Environment.ProcessId}-{Guid.NewGuid():N}";

        /// <summary>The active transport's token, if any.</summary>
        public ITransportServiceToken? TransportToken { get; set; }

        /// <summary>Tokens of all configured database providers.</summary>
        public List<IDatabaseServiceToken> DatabaseTokens { get; set; }

        /// <summary>The active cache provider's token, if any.</summary>
        public ICacheServiceToken? CacheToken { get; set; }

        /// <summary>Created by DI.</summary>
        /// <param name="databaseServiceTokens">All registered database tokens.</param>
        /// <param name="token">Transport token, if registered.</param>
        /// <param name="cacheToken">Cache token, if registered.</param>
        /// <param name="configOptions">Engine options; non-null enables <see cref="EngineEnabled"/>.</param>
        public AltruistServerContext(
            List<IDatabaseServiceToken> databaseServiceTokens,
            ITransportServiceToken? token = null,
            ICacheServiceToken? cacheToken = null,
            EngineConfigOptions? configOptions = null)
        {
            EngineEnabled = configOptions != null;
            TransportToken = token;
            DatabaseTokens = databaseServiceTokens ?? new();
            CacheToken = cacheToken;
        }

        /// <summary>Adds a portal endpoint path (duplicates ignored).</summary>
        /// <param name="endpoint">Endpoint path, e.g. <c>/game</c>.</param>
        public void AddEndpoint(string endpoint) => Endpoints.Add(endpoint);

        /// <summary>Throws when no endpoint or no transport token is set.</summary>
        /// <exception cref="ArgumentException">No endpoints, or no transport configured.</exception>
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

        /// <summary>Multi-line summary (addresses per endpoint plus token descriptions) used in the startup banner.</summary>
        public override string ToString()
        {
            var lines = new List<string>();
            var serverString = $"{ServerInfo.Protocol}://{ServerInfo.Host}:{ServerInfo.Port}";

            foreach (var endpoint in Endpoints)
            {
                lines.Add($"💻 Address: {serverString}{endpoint}");
            }

            if (!string.IsNullOrEmpty(TransportToken?.Description))
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


}

