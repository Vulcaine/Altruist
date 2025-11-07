// Web/Features/WebsocketFeatureProvider.cs
using System;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using Altruist.Features;
using Microsoft.Extensions.Configuration;

namespace Altruist.Web.Features
{
    public sealed class WebsocketFeatureProvider : IAltruistFeatureProvider
    {
        public string FeatureId => "websocket";

        public object Configure(object stage, IConfiguration config)
        {
            var root = new AltruistConfigOptions();
            config.GetSection("altruist").Bind(root);

            if (!string.Equals(root.Transport.Mode, "websocket", StringComparison.OrdinalIgnoreCase))
                return stage;

            // Normalize to a connection builder
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
            return closed.Invoke(websocketSetup, new object[] { path })!;
        }
    }

    public static class ModuleInitializer
    {
        [ModuleInitializer]
        public static void Init() => FeatureRegistry.Register(new WebsocketFeatureProvider());
    }
}
