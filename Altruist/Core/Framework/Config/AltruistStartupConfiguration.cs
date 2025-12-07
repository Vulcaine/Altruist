using System.Text;

using Altruist.Contracts;
using Altruist.Transport;
using Altruist.Web.Features;
using Altruist.Security;

using Microsoft.AspNetCore.Builder;
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
        private readonly string? _packetTransport;
        private readonly string _wsContextPath;

        // Dependencies we need at runtime
        private readonly ILoggerFactory _loggerFactory;
        private readonly IAltruistContext _settings;
        private readonly IServerStatus _appStatus;
        private readonly ITransport? _transport;

        public AltruistStartupConfiguration(
            ApplicationArgs args,

            // HTTP: present => we host controllers on same server
            [AppConfigValue("altruist:server:http:host", null)] string? httpHost,
            [AppConfigValue("altruist:server:http:port", null)] string? httpPort,
            [AppConfigValue("altruist:server:http:path", "/")] string httpPath,

            // Transport: if mode == websocket => mount WS endpoints on same HTTP server
            [AppConfigValue("altruist:server:transport:mode", null)] string? transportMode,
            [AppConfigValue("altruist:server:transport:config:path", "/ws")] string transportPath,

            ILoggerFactory loggerFactory,
            IAltruistContext settings,
            IServerStatus appStatus,
            ITransport? transport = null
        )
        {
            _args = args;

            _httpHost = NormalizeEmpty(httpHost);
            _httpPort = NormalizeEmpty(httpPort);
            _httpContextPath = NormalizePath(httpPath, defaultIfEmpty: "/");

            _packetTransport = NormalizeEmpty(transportMode);
            _wsContextPath = NormalizePath(transportPath, defaultIfEmpty: "/ws");

            _loggerFactory = loggerFactory;
            _settings = settings;
            _appStatus = appStatus;
            _transport = transport;
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
            // If there's no HTTP block, there's nothing to start here (WS requires HTTP server).
            if (string.IsNullOrWhiteSpace(_httpHost) || string.IsNullOrWhiteSpace(_httpPort))
                return;

            // Build the (single) HTTP server
            var builder = WebApplication.CreateBuilder(_args?.Args ?? Array.Empty<string>());

            builder.Logging.ClearProviders();

            // Bring all root DI registrations into the web host
            foreach (var d in rootServices)
            {
                if (d.ServiceType == typeof(IHostApplicationLifetime))
                    continue; // don't override internal host service

                builder.Services.Add(d);
            }

            // MVC controllers (they’ll be mounted under http base path)
            builder.Services.AddControllers();

            var app = builder.Build();
            var logger = app.Logger; // ⬅️ moved up so we can use it in WS validation

            // Base path for controllers (e.g., /api or /)
            if (_httpContextPath != "/" && !string.IsNullOrWhiteSpace(_httpContextPath))
            {
                app.UsePathBase(_httpContextPath);
            }

            // WebSockets only if the transport mode is websocket
            var useWebSocket = string.Equals(_packetTransport, "websocket", StringComparison.OrdinalIgnoreCase);
            if (useWebSocket)
            {
                var webSocketOptions = new WebSocketOptions
                {
                    KeepAliveInterval = TimeSpan.FromMinutes(2)
                };
                app.UseWebSockets(webSocketOptions);
            }

            app.UseRouting();
            app.MapControllers();                       // controllers live under PathBase if set
            app.UseMiddleware<ReadinessMiddleware>();   // health/readiness

            // Discover “portals” and mount them under the transport path
            if (useWebSocket && _transport is not null)
            {
                var portals = PortalDiscovery.Discover().Distinct().ToArray();

                // ─────────────────────────────────────────────────────────────
                // 1) VALIDATE SHIELDS PER WS ROUTE BEFORE REGISTERING
                //    - If multiple portals map to the same WS path and:
                //      • some are shielded, some are not  → ERROR
                //      • or shield types differ           → ERROR
                // ─────────────────────────────────────────────────────────────

                // Group portals by final WS path (transport base path + portal path)
                var groupedByPath = portals
                    .Select(p => new
                    {
                        PortalType = p.PortalType,
                        WsPath = CombinePaths(_wsContextPath, p.Path)
                    })
                    .GroupBy(x => x.WsPath, StringComparer.Ordinal);

                foreach (var group in groupedByPath)
                {
                    Type? expectedShieldType = null;
                    Type? firstPortalType = null;

                    foreach (var item in group)
                    {
                        var shieldAttr = item.PortalType
                            .GetCustomAttributes(inherit: true)
                            .OfType<ShieldAttribute>()
                            .FirstOrDefault();

                        var currentShieldType = shieldAttr?.GetType();

                        if (expectedShieldType is null)
                        {
                            expectedShieldType = currentShieldType;
                            firstPortalType = item.PortalType;
                            continue;
                        }

                        if (!Equals(expectedShieldType, currentShieldType))
                        {
                            var msg =
                                $"❌ Conflicting Shield configuration for WebSocket route '{group.Key}'.\n" +
                                $"   • First portal: {DependencyResolver.GetCleanName(firstPortalType!)} " +
                                $"      → shield: {expectedShieldType?.Name ?? "<none>"}\n" +
                                $"   • Portal: {DependencyResolver.GetCleanName(item.PortalType)} " +
                                $"      → shield: {currentShieldType?.Name ?? "<none>"}\n\n" +
                                "All portals mapped to the same WebSocket path must either:\n" +
                                "  - all be unshielded, OR\n" +
                                "  - all use the same ShieldAttribute type.\n";

                            DependencyResolver.FailAndExit(logger, msg);
                            throw new InvalidOperationException(msg);
                        }
                    }
                }

                // ─────────────────────────────────────────────────────────────
                // 2) AFTER VALIDATION, REGISTER ROUTES ON THE TRANSPORT
                // ─────────────────────────────────────────────────────────────
                foreach (var (type, path) in portals)
                {
                    // Prefix each discovered path with transport path
                    var wsMappedPath = CombinePaths(_wsContextPath, path);
                    _transport.UseTransportEndpoints(app, type, wsMappedPath);
                }

                // Let transport do any final routing hooks it needs
                _transport.RouteTraffic(app);
            }

            // Prefer “ws” scheme if WS enabled, otherwise “http”
            if (!int.TryParse(_httpPort, out var portNum))
                portNum = 8080;

            var scheme = useWebSocket ? "ws" : "http";
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
