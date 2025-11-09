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

using System.Reflection;
using Altruist.Features;
using Microsoft.Extensions.DependencyInjection;

namespace Altruist.Web.Features
{
    public sealed class WebsocketFeatureProvider : IAltruistFeatureProvider
    {
        public string FeatureId => "websocket";

        public object Configure(object stage, IServiceProvider services)
        {
            var root = services.GetRequiredService<AltruistConfigOptions>();

            if (!string.Equals(root.Transport.Mode, "websocket", StringComparison.OrdinalIgnoreCase))
                return stage;

            AltruistConnectionBuilder connection = stage switch
            {
                AltruistConnectionBuilder c => c,
                AltruistIntermediateBuilder i => i.NoEngine(),
                _ => throw new InvalidOperationException(
                    $"Websocket feature expected stage AltruistConnectionBuilder or AltruistIntermediateBuilder, but got {stage.GetType().Name}.")
            };

            // Attribute-only discovery
            var discovered = PortalDiscovery.Discover();
            // Apply transport and map discovered portals
            var next = connection.WithWebsocket(ws =>
            {
                object cur = ws;
                foreach (var d in discovered)
                {
                    cur = MapPortalRuntime(cur, d.PortalType, d.Path);
                }

                return (dynamic)cur;
            });

            return next;
        }

        private IEnumerable<Assembly> GetAssemblies()
        {
            throw new NotImplementedException();
        }

        /// <summary>
        /// Calls MapPortal&lt;P&gt;(string path) on the websocket setup object via reflection.
        /// </summary>
        private static object MapPortalRuntime(object websocketSetup, Type portalType, string path)
        {
            var mi = websocketSetup.GetType()
                .GetMethods(BindingFlags.Instance | BindingFlags.Public)
                .Where(m => m.Name == "MapPortal" && m.IsGenericMethodDefinition)
                .First(m =>
                {
                    var ga = m.GetGenericArguments();
                    if (ga.Length != 1) return false;
                    var ps = m.GetParameters();
                    return ps.Length == 1 && ps[0].ParameterType == typeof(string);
                });

            var closed = mi.MakeGenericMethod(portalType);
            return closed.Invoke(websocketSetup, [path])!;
        }
    }
}
