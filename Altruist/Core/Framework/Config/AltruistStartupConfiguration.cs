using System.Reflection;
using System.Text;

using Altruist.Contracts;
using Altruist.Security;
using Altruist.Transport;
using Altruist.Web.Features;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ApplicationParts;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Altruist
{
    [ServiceConfiguration(order: int.MaxValue)]
    public sealed class AltruistStartupConfiguration : IAltruistConfiguration
    {
        public bool IsConfigured { get; set; }

        private readonly ApplicationArgs _args;

        // HTTP config (optional – if unset we don't host HTTP)
        private readonly string? _httpHost;
        private readonly string? _httpPort;
        private readonly string _httpContextPath;

        // Packet transport (optional)
        // Removed: _packetTransport — replaced by _transports collection
        private readonly string _wsContextPath;

        // Dependencies we need at runtime
        private readonly ILoggerFactory _loggerFactory;
        private readonly IAltruistContext _settings;
        private readonly IServerStatus _appStatus;
        private readonly IEnumerable<ITransport> _transports;

        public AltruistStartupConfiguration(
            ApplicationArgs args,

            [AppConfigValue("altruist:server:http:host", null)] string? httpHost,
            [AppConfigValue("altruist:server:http:port", null)] string? httpPort,
            [AppConfigValue("altruist:server:http:path", "/")] string httpPath,

            [AppConfigValue("altruist:server:transport:websocket:path", "/ws")] string wsPath,

            ILoggerFactory loggerFactory,
            IAltruistContext settings,
            IServerStatus appStatus,
            IEnumerable<ITransport>? transports = null
        )
        {
            _args = args;

            _httpHost = NormalizeEmpty(httpHost);
            _httpPort = NormalizeEmpty(httpPort);
            _httpContextPath = NormalizePath(httpPath, defaultIfEmpty: "/");

            _wsContextPath = NormalizePath(wsPath, defaultIfEmpty: "/ws");

            _loggerFactory = loggerFactory;
            _settings = settings;
            _appStatus = appStatus;
            _transports = transports ?? [];
        }

        /// <summary>
        /// Configuration stage is a no-op here; we just need this type to be registered
        /// so that Bootstrap can resolve it later and call StartAsync.
        /// </summary>
        public Task Configure(IServiceCollection services) => Task.CompletedTask;

        /// <summary>
        /// Build and run the single HTTP server after all services and PostConstruct hooks are done.
        /// </summary>
        public async Task StartAsync(IServiceCollection rootServices, CancellationToken cancellationToken = default)
        {

            if (string.IsNullOrWhiteSpace(_httpHost) || string.IsNullOrWhiteSpace(_httpPort))
                return;

            var builder = WebApplication.CreateBuilder(_args?.Args ?? Array.Empty<string>());
            using var tempProvider = rootServices.BuildServiceProvider();

            var configSource = tempProvider.GetService<MutableConfigSource>();

            if (configSource == null)
                throw new InvalidOperationException("MutableConfigSource not registered.");

            // Insert into WebApplication builder
            builder.Configuration.Sources.Insert(0, configSource);
            builder.Logging.ClearProviders();

            foreach (var d in rootServices)
            {
                if (d.ServiceType == typeof(IHostApplicationLifetime))
                    continue;

                builder.Services.Add(d);
            }

            var mvcBuilder = builder.Services.AddControllers();

            // Automatically register all loaded assemblies that contain MVC controllers
            mvcBuilder.ConfigureApplicationPartManager(apm =>
            {
                var assemblies = AppDomain.CurrentDomain.GetAssemblies();

                foreach (var assembly in assemblies)
                {
                    if (assembly.IsDynamic)
                        continue;

                    bool hasController = false;

                    try
                    {
                        hasController = assembly
                            .GetExportedTypes()
                            .Any(t =>
                                t.IsClass &&
                                !t.IsAbstract &&
                                typeof(ControllerBase).IsAssignableFrom(t));
                    }
                    catch (ReflectionTypeLoadException)
                    {
                        // some assemblies may fail GetExportedTypes; just skip them
                        continue;
                    }

                    if (hasController)
                    {
                        // avoid duplicates
                        if (!apm.ApplicationParts.OfType<AssemblyPart>()
                                .Any(p => p.Assembly == assembly))
                        {
                            apm.ApplicationParts.Add(new AssemblyPart(assembly));
                        }
                    }
                }
            });

            var app = builder.Build();
            var logger = app.Logger;

            app.UseDeveloperExceptionPage();

            if (_httpContextPath != "/" && !string.IsNullOrWhiteSpace(_httpContextPath))
            {
                app.UsePathBase(_httpContextPath);
            }

            var hasWebSocket = _transports.Any(t => t.TransportType == "websocket");
            if (hasWebSocket)
            {
                app.UseWebSockets(new WebSocketOptions
                {
                    KeepAliveInterval = TimeSpan.FromMinutes(2)
                });
            }

            app.UseRouting();
            app.UseAuthentication();
            app.UseAuthorization();
            app.MapControllers();
            app.UseMiddleware<ReadinessMiddleware>();

            // Discover portals once — shared across all transports
            var portals = PortalDiscovery.Discover().Distinct().ToArray();

            // Register portals on every active transport
            foreach (var transport in _transports)
            {
                if (transport.TransportType == "websocket")
                {
                    // WebSocket: prefix paths, validate shields, register routes
                    ValidateWebSocketShields(portals, logger);
                    foreach (var (type, path) in portals)
                    {
                        var wsMappedPath = CombinePaths(_wsContextPath, path);
                        transport.UseTransportEndpoints(app, type, wsMappedPath);
                    }
                    transport.RouteTraffic(app);
                }
                else
                {
                    // TCP/UDP: register with connection manager
                    transport.UseTransportEndpoints(app, typeof(IConnectionManager), "/game");
                }

                logger.LogInformation("{Transport} transport started.", transport.TransportType.ToUpper());
            }

            if (!int.TryParse(_httpPort, out var portNum))
                portNum = 8080;

            var scheme = hasWebSocket ? "ws" : "http";
            _settings.ServerInfo = new ServerInfo("Altruist Server", scheme, _httpHost!, portNum);

            var logBuilder = BuildStartupLog(_settings);
            Console.WriteLine("\n" + logBuilder + "\n");

            if (_appStatus != null)
            {
                Console.WriteLine(_appStatus.ToString());
                if (_appStatus.Status != ReadyState.Alive)
                {
                    logger.LogWarning("🕒 Services still warming up. No inbound/outbound packets yet; engine start is deferred.");
                }
            }

            // Listen & serve (this will block until shutdown)
            var connectionString = $"http://{_httpHost}:{portNum}";
            await app.RunAsync(connectionString);
        }

        // ---------- helpers ----------

        private static string NormalizeEmpty(string? s) => string.IsNullOrWhiteSpace(s) ? "" : s.Trim();

        private static string NormalizePath(string? path, string defaultIfEmpty = "/")
        {
            var p = string.IsNullOrWhiteSpace(path) ? defaultIfEmpty : path!.Trim();
            if (!p.StartsWith("/"))
                p = "/" + p;
            if (p.Length > 1 && p.EndsWith("/"))
                p = p.TrimEnd('/');
            return p;
        }

        private static void ValidateWebSocketShields(
            IEnumerable<PortalDiscovery.Descriptor> portals, ILogger logger)
        {
            var grouped = portals
                .GroupBy(p => p.Path, StringComparer.Ordinal);

            foreach (var group in grouped)
            {
                Type? expectedShieldType = null;
                Type? firstPortalType = null;

                foreach (var descriptor in group)
                {
                    var portalType = descriptor.PortalType;
                    var shieldAttr = portalType
                        .GetCustomAttributes(inherit: true)
                        .OfType<ShieldAttribute>()
                        .FirstOrDefault();

                    var currentShieldType = shieldAttr?.GetType();

                    if (expectedShieldType is null)
                    {
                        expectedShieldType = currentShieldType;
                        firstPortalType = portalType;
                        continue;
                    }

                    if (!Equals(expectedShieldType, currentShieldType))
                    {
                        var msg =
                            $"Conflicting Shield for WebSocket route '{group.Key}'.\n" +
                            $"  {DependencyResolver.GetCleanName(firstPortalType!)} -> {expectedShieldType?.Name ?? "none"}\n" +
                            $"  {DependencyResolver.GetCleanName(portalType)} -> {currentShieldType?.Name ?? "none"}";
                        DependencyResolver.FailAndExit(logger, msg);
                        throw new InvalidOperationException(msg);
                    }
                }
            }
        }

        private static string CombinePaths(string basePath, string child)
        {
            var a = NormalizePath(basePath);
            var b = NormalizePath(child);
            if (a == "/")
                return b; // root + /x => /x
            if (b == "/")
                return a; // /a + / => /a
            return a + (b == "/" ? "" : b);
        }

        private static string BuildStartupLog(IAltruistContext settings)
        {
            var frameLine = new string('═', 80);
            string PortaledText(string text) => $"{text}".PadLeft((80 + text.Length) / 2).PadRight(80);

            var logBuilder = new StringBuilder();
            logBuilder.AppendLine(PortaledText(@"
 █████╗ ██╗  ████████╗██████╗ ██╗   ██╗██╗███████╗████████╗    ██╗   ██╗ ██╗
██╔══██╗██║  ╚══██╔══╝██╔══██╗██║   ██║██║██╔════╝╚══██╔══╝    ██║   ██║███║
███████║██║     ██║   ██████╔╝██║   ██║██║███████╗   ██║       ██║   ██║╚██║
██╔══██║██║     ██║   ██╔══██╗██║   ██║██║╚════██║   ██║       ╚██╗ ██╔╝ ██║
██║  ██║███████╗██║   ██║  ██║╚██████╔╝██║███████║   ██║        ╚████╔╝  ██║
╚═╝  ╚═╝╚══════╝╚═╝   ╚═╝  ╚═╝ ╚═════╝ ╚═╝╚══════╝   ╚═╝         ╚═══╝   ╚═╝
"));
            logBuilder.AppendLine(frameLine);

            var settingsLines = settings.ToString()!.Replace('\r', ' ').Split('\n');
            int lineWidth = 50;

            if (settings.Endpoints.Count() > 0)
            {
                logBuilder.AppendLine("╔════════════════════════════════════════════════════╗");
                logBuilder.AppendLine("║ The Portals Are Open! Connect At:                  ║");
                logBuilder.AppendLine("║".PadRight(lineWidth + 3, '-') + "║");
                foreach (var line in settingsLines)
                {
                    int currentLineLength = "║ ".Length + line.Length + " ║".Length;
                    string paddedLine = $"║ {line.PadRight(currentLineLength + (lineWidth - currentLineLength))} ║";
                    logBuilder.AppendLine(paddedLine);
                }
                logBuilder.AppendLine("║".PadRight(lineWidth + 3, '-') + "║");
                logBuilder.AppendLine("║ ✨ Welcome, traveler! 🧙                           ║");
                logBuilder.AppendLine("╚════════════════════════════════════════════════════╝");
            }


            return logBuilder.ToString();
        }
    }
}
