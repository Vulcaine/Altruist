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
            app.UseAuthentication();
            app.UseAuthorization();
            app.MapControllers();
            app.UseMiddleware<ReadinessMiddleware>();

            if (useWebSocket && _transport is not null)
            {
                var portals = PortalDiscovery.Discover().Distinct().ToArray();

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

            // Start TCP transport if configured (non-WebSocket mode)
            var useTcp = string.Equals(_packetTransport, "tcp", StringComparison.OrdinalIgnoreCase);
            if (useTcp && _transport is not null)
            {
                _transport.UseTransportEndpoints(app, typeof(IConnectionManager), "/game");
                logger.LogInformation("TCP transport started.");
            }

            // Prefer "ws" scheme if WS enabled, otherwise "http"
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
