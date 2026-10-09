// Altruist.Boot/AltruistApplication.cs
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Altruist
{
    /// <summary>
    /// Main entry point of an Altruist server application: loads configuration, registers everything discovered by
    /// attribute (services, beans, configuration steps, portals), runs <see cref="PostConstructAttribute"/> hooks and
    /// <see cref="AltruistModuleAttribute"/> modules, then starts the HTTP host and transports and blocks until shutdown.
    /// </summary>
    /// <remarks>
    /// Use this for servers. For a process that only needs the DI container (tools, workers, no networking) use
    /// <see cref="AltruistDI.Run"/> instead. Configuration comes from <c>config.yml</c>, <c>config.{env}.yml</c>, environment
    /// variables and <paramref name="args"/> (see <see cref="AppConfigLoader"/>). HTTP is only hosted when
    /// <c>altruist:server:http:host</c> and <c>altruist:server:http:port</c> are set; otherwise <see cref="Run"/> returns after
    /// startup completes.
    /// </remarks>
    /// <example>
    /// <code>
    /// public static class Program
    /// {
    ///     public static Task Main(string[] args) =&gt; AltruistApplication.Run(args);
    /// }
    /// </code>
    /// </example>
    public static class AltruistApplication
    {
        /// <summary>The configuration loaded by <see cref="Run"/>; unset (null) before it is called. Prefer injecting <see cref="IConfiguration"/>.</summary>
        public static IConfiguration Configuration { get; private set; } = default!;

        /// <summary>Boots the application (see the type remarks) and runs until the host shuts down.</summary>
        /// <param name="args">Command-line arguments, used as the highest-priority configuration source (<c>--altruist:server:http:port=8080</c>) if configuration was not loaded earlier.</param>
        public static async Task Run(string[]? args = null)
        {
            var cfg = AppConfigLoader.Load(args);
            Configuration = cfg;

            AltruistBootstrap.Services.AddSingleton(cfg);
            await AltruistBootstrap.Bootstrap();
        }
    }
}
