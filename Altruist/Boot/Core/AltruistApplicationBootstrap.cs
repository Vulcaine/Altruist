// Altruist.Boot/AltruistApplication.cs
using Altruist.Features;
using Microsoft.Extensions.Configuration;

namespace Altruist
{
    public static class AltruistApplication
    {
        public static IConfiguration Configuration { get; private set; } = default!;

        public static void Run(string[]? args = null)
        {
            // 1) Load configuration
            var env = Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT") ?? "Production";
            var basePath = AppContext.BaseDirectory;

            var cfg = new ConfigurationBuilder()
                .SetBasePath(basePath)
                .AddYamlFile(Path.Combine(basePath, "config.yml"), optional: false, reloadOnChange: true)
                .AddYamlFile(Path.Combine(basePath, $"config.{env}.yml"), optional: true, reloadOnChange: true)
                .AddEnvironmentVariables(prefix: "ALTRUIST__")
                .AddCommandLine(args ?? Array.Empty<string>())
                .Build();

            Configuration = cfg;

            // 2) Bind root options (with GameConfigOptions nested)
            var opts = new AltruistConfigOptions();
            cfg.GetSection("altruist").Bind(opts);

            // 3) Determine requested features from config
            var requested = DecideRequestedFeatures(opts);

            // 4) Start at the Intermediate stage
            object stage = AltruistBuilder.Create(args ?? Array.Empty<string>());

            // 5) Let providers advance the pipeline (in a stable order)
            foreach (var feature in requested)
            {
                var provider = FeatureRegistry.Find(feature);
                if (provider is null)
                {
                    var suggestion = feature switch
                    {
                        "game-engine" => "Add the Altruist.Gaming module to enable the game engine.",
                        "websocket" => "Add the Altruist.Web module to enable WebSocket transport.",
                        _ => $"Add a module that provides feature '{feature}'."
                    };
                    throw new InvalidOperationException(
                        $"Feature '{feature}' requested by config, but no module was found to provide it. {suggestion}");
                }

                var next = provider.Configure(stage, cfg);
                if (next is null)
                    throw new InvalidOperationException($"Feature provider for '{feature}' returned a null stage.");

                stage = next;
            }

            // 6) Finalize: we expect either IAfterConnectionBuilder or AltruistWebApplicationBuilder
            if (stage is IAfterConnectionBuilder afterConn)
            {
                var web = afterConn.WebApp(b =>
                {
                    // If your WebApp builder reads host/port from Configuration, set them here.
                    b.Configuration["altruist:server:host"] = opts.Server.Host;
                    b.Configuration["altruist:server:port"] = opts.Server.Port.ToString();
                    return b;
                });

                web.StartServer();
                return;
            }

            if (stage is AltruistWebApplicationBuilder webApp)
            {
                webApp.StartServer();
                return;
            }

            throw new InvalidOperationException(
                "Configuration did not produce a runnable application. " +
                "Ensure at least one transport is configured (e.g., 'transport.mode: websocket') " +
                "and the corresponding module is referenced.");
        }

        private static IReadOnlyList<string> DecideRequestedFeatures(AltruistConfigOptions opts)
        {
            var features = new List<string>();

            // Game feature is requested if any game options are present AND meaningful
            var game = opts.Game;
            var wantsGame =
                game != null &&
                (
                    (game.Worlds != null && game.Worlds.Count > 0) ||
                    game.Engine != null // engine settings supplied, even if worlds are empty (provider can validate further)
                );

            if (wantsGame)
                features.Add("game-engine");

            // Transport requests
            if (string.Equals(opts.Transport.Mode, "websocket", StringComparison.OrdinalIgnoreCase))
                features.Add("websocket");

            // Sort so engine runs before transport (stable order)
            var order = new[] { "game-engine", "websocket" };
            return features.OrderBy(f => Array.IndexOf(order, f)).ToArray();
        }
    }
}
