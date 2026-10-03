using System.Reflection;
using System.Text;
using System.Diagnostics;

using Altruist.Contracts;
using Altruist.Security;
using Altruist.Transport;
using Altruist.Web.Features;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ApplicationParts;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.Extensions.Configuration;
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
        /// Blocks until shutdown.
        /// </summary>
        public Task StartAsync(IServiceCollection rootServices, CancellationToken cancellationToken = default)
            => StartAsync(rootServices, bootstrapProvider: null, cancellationToken);

        public async Task StartAsync(IServiceCollection rootServices, IServiceProvider? bootstrapProvider, CancellationToken cancellationToken = default)
        {
            var app = await BuildAndStartAsync(rootServices, bootstrapProvider, cancellationToken);
            if (app is null) return;
            await app.WaitForShutdownAsync(cancellationToken);
        }

        /// <summary>
        /// Same setup as <see cref="StartAsync"/> but returns the live <see cref="WebApplication"/>
        /// after listeners are bound, instead of blocking on shutdown. Callers (notably the
        /// test framework) can let it run in the background and shut it down explicitly via
        /// the returned <c>WebApplication</c>'s <c>StopAsync</c>/<c>DisposeAsync</c>.
        ///
        /// <para>Returns <c>null</c> if HTTP host/port is unconfigured (matches
        /// <see cref="StartAsync"/>'s no-op semantic).</para>
        /// </summary>
        public Task<WebApplication?> BuildAndStartAsync(IServiceCollection rootServices, CancellationToken cancellationToken = default)
            => BuildAndStartAsync(rootServices, bootstrapProvider: null, cancellationToken);

        public async Task<WebApplication?> BuildAndStartAsync(IServiceCollection rootServices, IServiceProvider? bootstrapProvider, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(_httpHost) || string.IsNullOrWhiteSpace(_httpPort))
                return null;

            var builder = WebApplication.CreateBuilder(_args?.Args ?? Array.Empty<string>());
            using var tempProvider = rootServices.BuildServiceProvider();
            // The provider whose pre-built singletons we share with WebApplication.
            // Production passes the bootstrap provider (the one whose [PostConstruct]
            // hooks ran). Tests inherit the same — LiveServerHandle's _provider.
            // Falls back to tempProvider only when callers haven't been updated.
            var sharingProvider = bootstrapProvider ?? tempProvider;

            var configSource = tempProvider.GetService<MutableConfigSource>();

            if (configSource == null)
                throw new InvalidOperationException("MutableConfigSource not registered.");

            // Insert into WebApplication builder
            builder.Configuration.Sources.Insert(0, configSource);
            builder.Logging.ClearProviders();

            // Promote every Singleton-with-factory descriptor to an instance-based
            // registration backed by the bootstrap provider's already-built instance.
            // Otherwise WebApplication's provider builds a fresh duplicate of each
            // singleton (per-provider singleton lifetime in MEDI), and none of those
            // duplicates would have had [PostConstruct] run on them. Symptoms in
            // production: IServerStatus stuck at Starting → ReadinessMiddleware 503s
            // forever; portals/sessions/connection-managers diverge from the bootstrap
            // state so TCP packets dispatch against fresh handlers and silently drop.
            // Restores the cross-provider singleton sharing that the old static
            // _singletonCache provided implicitly. Test child providers (per-method
            // DI containers) still build their own fresh singletons so mocks still
            // substitute cleanly.
            // Enumerable registrations (several descriptors for one service type, e.g. every
            // IHostedService) must map descriptor i to instance i of GetServices: GetService
            // returns only the LAST one, so promoting each descriptor with it registered the last
            // implementation N times and dropped the others (two hosted services -> the last one
            // started twice, the first never).
            var seenOfType = new Dictionary<Type, int>();
            var countOfType = rootServices
                .Where(x => !x.IsKeyedService)
                .GroupBy(x => x.ServiceType)
                .ToDictionary(g => g.Key, g => g.Count());
            foreach (var d in rootServices)
            {
                if (d.ServiceType == typeof(IHostApplicationLifetime))
                    continue;
                var ordinal = 0;
                if (!d.IsKeyedService)
                {
                    seenOfType.TryGetValue(d.ServiceType, out ordinal);
                    seenOfType[d.ServiceType] = ordinal + 1;
                }

                // Promote singletons (factory- AND type-based) to instance-based
                // registrations backed by the bootstrap provider's already-built
                // instances. Otherwise WebApplication's provider builds a fresh
                // duplicate, and singleton-state (PortalWarmup's gate registry,
                // ConnectionManager's connection table, CharacterService's
                // _playersByClientId, etc.) silently diverges between the two
                // providers — the bootstrap's instance has the [PostConstruct]
                // wiring done, but the TCP listener (started by WebApplication's
                // provider) ends up dispatching against the FRESH duplicate
                // whose state is empty.
                if (d.Lifetime == ServiceLifetime.Singleton
                    && !d.ServiceType.IsGenericTypeDefinition
                    && !d.IsKeyedService
                    && (d.ImplementationFactory is not null || d.ImplementationType is not null))
                {
                    try
                    {
                        var instance = countOfType.GetValueOrDefault(d.ServiceType) > 1
                            ? sharingProvider.GetServices(d.ServiceType).ElementAtOrDefault(ordinal)
                            : sharingProvider.GetService(d.ServiceType);
                        if (instance is not null)
                        {
                            builder.Services.AddSingleton(d.ServiceType, instance);
                            continue;
                        }
                    }
                    catch
                    {
                        // Fall through — copy the original descriptor unchanged.
                    }
                }

                builder.Services.Add(d);
            }

            var mvcBuilder = builder.Services.AddControllers();
            var conditionLog = _loggerFactory.CreateLogger<AltruistStartupConfiguration>();

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

                // [ConditionalOnConfig] gates controllers too (e.g. the E2E reset endpoint, which
                // truncates every vault table, must not exist unless altruist:e2e:enabled is true).
                var defaultProvider = apm.FeatureProviders.OfType<ControllerFeatureProvider>().FirstOrDefault();
                if (defaultProvider is not null)
                    apm.FeatureProviders.Remove(defaultProvider);
                apm.FeatureProviders.Add(new ConditionalControllerFeatureProvider(builder.Configuration, conditionLog));
            });

            var app = builder.Build();
            var logger = app.Logger;

            // Stack traces only in Development: elsewhere an unhandled error is a bare 500.
            if (app.Environment.IsDevelopment())
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
            app.Use(async (context, next) =>
            {
                var recorder = context.RequestServices.GetService<IDashboardNetworkRecorder>();
                if (recorder is null
                    || !recorder.CaptureHttp
                    || context.WebSockets.IsWebSocketRequest
                    || IsDashboardDevtoolsEndpoint(context.Request.Path.Value))
                {
                    await next();
                    return;
                }

                var watch = Stopwatch.StartNew();
                string? error = null;
                try
                {
                    await next();
                }
                catch (Exception ex)
                {
                    error = ex.Message;
                    throw;
                }
                finally
                {
                    watch.Stop();
                    await recorder.RecordAsync(new DashboardNetworkEvent
                    {
                        Kind = "http",
                        Direction = "inbound",
                        Transport = "http",
                        Method = context.Request.Method,
                        Path = context.Request.Path.Value,
                        StatusCode = context.Response.StatusCode,
                        Route = context.GetEndpoint()?.DisplayName,
                        DurationMs = watch.Elapsed.TotalMilliseconds,
                        Error = error
                    });
                }
            });
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
                    // WebSocket: portal paths are exact. Include any desired prefix in [Portal(...)].
                    ValidateWebSocketShields(portals, logger);
                    foreach (var (type, path) in portals)
                    {
                        // Disabled portals get no route: otherwise their path would accept
                        // unauthenticated sockets that can reach every registered gate.
                        if (!DependencyResolver.ShouldRegister(type, app.Configuration, logger))
                            continue;
                        var wsMappedPath = NormalizePath(path);
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

            // Bind listeners (non-blocking). Caller decides whether to await shutdown
            // — production calls <see cref="StartAsync"/> which then awaits
            // <c>WaitForShutdownAsync</c>; tests keep the handle and stop explicitly.
            var connectionString = $"http://{_httpHost}:{portNum}";
            app.Urls.Add(connectionString);
            await app.StartAsync(cancellationToken);
            return app;
        }

        // ---------- helpers ----------

        private static string NormalizeEmpty(string? s) => string.IsNullOrWhiteSpace(s) ? "" : s.Trim();

        private static bool IsDashboardDevtoolsEndpoint(string? path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return false;

            return path.StartsWith("/dashboard/v1/network", StringComparison.OrdinalIgnoreCase) ||
                   path.StartsWith("/dashboard/v1/performance", StringComparison.OrdinalIgnoreCase);
        }

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

    /// <summary>MVC controller discovery that honours [ConditionalOnConfig] on controller types.</summary>
    internal sealed class ConditionalControllerFeatureProvider : ControllerFeatureProvider
    {
        private readonly IConfiguration _configuration;
        private readonly ILogger _logger;

        public ConditionalControllerFeatureProvider(IConfiguration configuration, ILogger logger)
        {
            _configuration = configuration;
            _logger = logger;
        }

        protected override bool IsController(TypeInfo typeInfo) =>
            base.IsController(typeInfo) && DependencyResolver.ShouldRegister(typeInfo.AsType(), _configuration, _logger);
    }
}
