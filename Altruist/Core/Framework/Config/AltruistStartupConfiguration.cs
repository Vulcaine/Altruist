using System.Text;

using Altruist.Contracts;
using Altruist.Transport;
using Altruist.Web.Features;

using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Altruist
{
    [AppConfiguration(order: int.MaxValue)]
    public sealed class AltruistStartupConfiguration : IAltruistConfiguration
    {
        private readonly ApplicationArgs _args;

        // HTTP config (optional – if unset we don't host HTTP)
        private readonly string? _httpHost;
        private readonly string? _httpPort;
        private readonly string _httpContextPath;

        // Packet transport (optional)
        private readonly string? _packetTransport;
        private readonly string _wsContextPath;

        public AltruistStartupConfiguration(
    ApplicationArgs args,

    // HTTP: present => we host controllers on same server
    [AppConfigValue("altruist:server:http:host", null)] string? httpHost,
    [AppConfigValue("altruist:server:http:port", null)] string? httpPort,
    [AppConfigValue("altruist:server:http:path", "/")] string httpPath,

    // Transport: if mode == websocket => mount WS endpoints on same HTTP server
    [AppConfigValue("altruist:server:transport:mode", null)] string? transportMode,
    [AppConfigValue("altruist:server:transport:config:path", "/ws")] string transportPath,

    IServiceCollection rootServices,
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

            _packetTransport = NormalizeEmpty(transportMode);   // reuse field name; now means server.transport.mode
            _wsContextPath = NormalizePath(transportPath, defaultIfEmpty: "/ws"); // now server.transport.config.path

            // If there's no HTTP block, there's nothing to start here (WS requires HTTP server).
            if (string.IsNullOrWhiteSpace(_httpHost) || string.IsNullOrWhiteSpace(_httpPort))
                return;

            // Build the (single) HTTP server
            var builder = WebApplication.CreateBuilder(_args?.Args ?? Array.Empty<string>());
            builder.Logging.ClearProviders();
            foreach (var d in rootServices)
                builder.Services.Add(d);

            // MVC controllers (they’ll be mounted under http base path)
            builder.Services.AddControllers();

            var app = builder.Build();

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
            app.MapControllers();                    // controllers live under PathBase if set
            app.UseMiddleware<ReadinessMiddleware>(); // health/readiness

            // Discover “portals” and mount them under the transport path
            if (useWebSocket && transport is not null)
            {
                var portals = PortalDiscovery.Discover().Distinct().ToArray();
                foreach (var (type, path) in portals)
                {
                    // Prefix each discovered path with transport path
                    var wsMappedPath = CombinePaths(_wsContextPath, path);
                    transport.UseTransportEndpoints(app, type, wsMappedPath);
                }

                // Let transport do any final routing hooks it needs
                transport.RouteTraffic(app);
            }

            // ServerInfo + pretty banner
            var logger = loggerFactory.CreateLogger<AltruistStartupConfiguration>();
            if (!int.TryParse(_httpPort, out var portNum))
                portNum = 8080;

            // Prefer “ws” scheme if WS enabled, otherwise “http”
            var scheme = useWebSocket ? "ws" : "http";
            settings.ServerInfo = new ServerInfo("Altruist Server", scheme, _httpHost!, portNum);

            var logBuilder = BuildStartupLog(settings);
            Console.WriteLine("\n" + logBuilder + "\n");

            if (appStatus != null)
            {
                Console.WriteLine(appStatus.ToString());
                if (appStatus.Status != ReadyState.Alive)
                {
                    logger.LogWarning("🕒 Services still warming up. No inbound/outbound packets yet; engine start is deferred.");
                }
            }

            // Event handlers discovery after DI is fully built
            EventHandlerRegistry<IPortal>.ScanAndRegisterHandlers(app.Services);

            // Listen & serve
            var connectionString = $"http://{_httpHost}:{portNum}";
            app.Run(connectionString);
        }

        public Task Configure(IServiceCollection services) => Task.CompletedTask;

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

            return logBuilder.ToString();
        }
    }
}
