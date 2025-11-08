// Altruist.Boot/AltruistApplication.cs
using System.Reflection;
using Altruist.Features;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyModel;
using Microsoft.Extensions.Logging;

namespace Altruist
{
    public static class AltruistApplication
    {
        public static IConfiguration Configuration { get; private set; } = default!;

        public static void Run(string[]? args = null)
        {
            AltruistBootstrap.Bootstrap();

            var cfg = AppConfigLoader.Load(args);
            Configuration = cfg;

            AltruistBootstrap.Services.AddSingleton<IConfiguration>(cfg);

            var sp = AltruistBootstrap.Services.BuildServiceProvider();
            var logger = sp.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(AltruistApplication));
            var opts = sp.GetRequiredService<AltruistConfigOptions>();

            var requested = DecideRequestedFeatures(opts);

            object stage = AltruistBuilder.Create(args ?? Array.Empty<string>());

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

                var next = provider.Configure(stage, sp);
                if (next is null)
                    throw new InvalidOperationException($"Feature provider for '{feature}' returned a null stage.");

                stage = next;
            }

            if (stage is IAfterConnectionBuilder afterConn)
            {
                var web = afterConn.WebApp(b =>
                {
                    try
                    {
                        var useHost = b.GetType().GetMethod("UseHost", BindingFlags.Public | BindingFlags.Instance);
                        var usePort = b.GetType().GetMethod("UsePort", BindingFlags.Public | BindingFlags.Instance);
                        useHost?.Invoke(b, [opts.Server.Host]);
                        usePort?.Invoke(b, [opts.Server.Port]);
                    }
                    catch { }
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

            var game = opts.Game;
            var wantsGame =
                game is not null &&
                (
                    (game.Worlds?.Items is not null && game.Worlds.Items.Count > 0) ||
                    game.Engine is not null
                );

            if (wantsGame)
                features.Add("game-engine");

            if (string.Equals(opts.Transport.Mode, "websocket", StringComparison.OrdinalIgnoreCase))
                features.Add("websocket");

            var order = new[] { "game-engine", "websocket" };
            return features.OrderBy(f => Array.IndexOf(order, f)).ToArray();
        }

        private static void EnsureFeatureAssembliesLoaded()
        {
            var ctx = DependencyContext.Default;
            if (ctx is null) return;

            foreach (var lib in ctx.CompileLibraries.Where(l =>
                         l.Name.StartsWith("Altruist.", StringComparison.OrdinalIgnoreCase)))
            {
                try { Assembly.Load(new AssemblyName(lib.Name)); }
                catch { }
            }

            var dir = AppContext.BaseDirectory;
            foreach (var path in Directory.EnumerateFiles(dir, "Altruist.*.dll"))
            {
                try
                {
                    var name = AssemblyName.GetAssemblyName(path);
                    if (AppDomain.CurrentDomain.GetAssemblies().All(a => a.GetName().Name != name.Name))
                        Assembly.Load(name);
                }
                catch { }
            }
        }
    }
}
