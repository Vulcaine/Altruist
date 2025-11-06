// Web/Features/WebsocketFeatureProvider.cs
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
            var opts = new AltruistConfigOptions();
            config.GetSection("altruist").Bind(opts);

            // Not selected => return stage unchanged
            if (!string.Equals(opts.Transport.Mode, "websocket", StringComparison.OrdinalIgnoreCase))
                return stage;

            // Ensure we have an AltruistConnectionBuilder to call WithWebsocket on
            AltruistConnectionBuilder connectionBuilder = stage switch
            {
                AltruistConnectionBuilder c => c,
                AltruistIntermediateBuilder i => i.NoEngine(),
                _ => throw new InvalidOperationException(
                    $"Websocket feature expected stage AltruistConnectionBuilder or AltruistIntermediateBuilder, but got {stage.GetType().Name}.")
            };

            // Apply the transport; returns IAfterConnectionBuilder
            var afterConn = connectionBuilder.WithWebsocket(ws => ws /* .MapPortal<...>("/...") */);

            // Return the next stage
            return afterConn;
        }
    }

    public static class ModuleInitializer
    {
        [ModuleInitializer]
        public static void Init()
        {
            FeatureRegistry.Register(new WebsocketFeatureProvider());
        }
    }
}
