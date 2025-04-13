using System.Reflection;
using System.Text;
using Altruist.Codec;
using Altruist.Contracts;
using Altruist.Database;
using Altruist.InMemory;
using Altruist.Transport;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Hosting.Internal;
using Microsoft.Extensions.Logging;

namespace Altruist
{
    public static class WebApiHelper
    {
        public static WebApplicationBuilder Create(string[] args, IServiceCollection collection)
        {
            var builder = WebApplication.CreateBuilder(args);
            builder.Logging.ClearProviders();

            foreach (var service in collection)
            {
                builder.Services.Add(service);
            }

            return builder;
        }
    }

    public class AltruistBuilder
    {
        private IServiceCollection Services { get; } = new ServiceCollection();
        private string[] _args;
        public IAltruistContext Settings { get; } = new AltruistServerContext();

        private AltruistBuilder(string[] args, Func<IServiceCollection, IServiceCollection>? serviceBuilder = null)
        {
            Services = serviceBuilder != null ? serviceBuilder.Invoke(new ServiceCollection()) : new ServiceCollection();
            _args = args;
            Services.AddLogging(loggingBuilder =>
            {
                loggingBuilder.ClearProviders();
                var frameworkVersion = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "1";

                loggingBuilder.AddProvider(new AltruistLoggerProvider(frameworkVersion));
            });
            // Add core services
            Services.AddSingleton<IHostEnvironment>(new HostingEnvironment
            {
                EnvironmentName = Environments.Development,
                ApplicationName = "Altruist",
                ContentRootPath = Directory.GetCurrentDirectory()
            });
            Services.AddSingleton(Services);
            Services.AddSingleton(Settings);
            Services.AddSingleton<ClientSender>();

            Services.AddSingleton<RoomSender>();
            Services.AddSingleton<BroadcastSender>();
            Services.AddSingleton<ClientSynchronizator>();
            // Setup cache
            Services.AddSingleton<InMemoryCache>();
            Services.AddSingleton<IMemoryCacheProvider>(sp => sp.GetRequiredService<InMemoryCache>());
            Services.AddSingleton<ICacheProvider>(sp => sp.GetRequiredService<InMemoryCache>());

            Services.AddSingleton<IAltruistRouter, InMemoryDirectRouter>();
            Services.AddSingleton<ICodec, JsonCodec>();
            Services.AddSingleton<IDecoder, JsonMessageDecoder>();
            Services.AddSingleton<IEncoder, JsonMessageEncoder>();
            Services.AddSingleton<IConnectionStore, InMemoryConnectionStore>();

            Services.AddSingleton<IPortalContext, PortalContext>();
            Services.AddSingleton<VaultRepositoryFactory>();
            Services.AddSingleton<DatabaseProviderFactory>();
            Services.AddSingleton(sp => new LoadSyncServicesAction(sp));
            Services.AddSingleton<IAction>(sp => sp.GetRequiredService<LoadSyncServicesAction>());
            Services.AddSingleton<IAppStatus, AppStatus>();
        }

        public static AltruistEngineBuilder Create(string[] args, Func<IServiceCollection, IServiceCollection>? serviceBuilder = null) => new AltruistBuilder(args, serviceBuilder).ToConnectionBuilder();

        private AltruistEngineBuilder ToConnectionBuilder() => new AltruistEngineBuilder(Services, Settings, _args);
    }

    // Step 1: Choose Transport
    public class AltruistConnectionBuilder
    {
        public readonly IServiceCollection Services;
        protected readonly IAltruistContext Settings;

        private string[] _args;

        internal AltruistConnectionBuilder(IServiceCollection services, IAltruistContext settings, string[] args)
        {
            Services = services;
            Settings = settings;
            _args = args;
        }

        public AltruistCacheBuilder SetupTransport<TTransportConnectionSetup>(ITransportServiceToken token, Func<TTransportConnectionSetup, TTransportConnectionSetup> setup) where TTransportConnectionSetup : class, ITransportConnectionSetup<TTransportConnectionSetup>
        {
            Settings.TransportToken = token;
            var serviceCollection = Services.AddSingleton<TTransportConnectionSetup>();
            var setupInstance = serviceCollection.BuildServiceProvider().GetService<TTransportConnectionSetup>();

            if (setup != null)
            {
                setupInstance = setup(setupInstance!);
            }

            SetupTransport(token, setupInstance!);
            return new AltruistCacheBuilder(Services, Settings, _args);
        }

        private void SetupTransport<TTransportConnectionSetup>(ITransportServiceToken token, TTransportConnectionSetup instance) where TTransportConnectionSetup : class, ITransportConnectionSetup<TTransportConnectionSetup>
        {
            Settings.TransportToken = token;
            token.Configuration.Configure(Services);
            instance.Build(Settings);
            // readding the built instance
            Services.AddSingleton(instance);
        }

        public void SetupTransport<TTransportConnectionSetup>(ITransportServiceToken token) where TTransportConnectionSetup : class, ITransportConnectionSetup<TTransportConnectionSetup>
        {
            token.Configuration.Configure(Services);
            var setupInstance = Services.BuildServiceProvider()
                .GetRequiredService<TTransportConnectionSetup>();
            SetupTransport(token, setupInstance);
        }
    }

    public interface IAfterConnectionBuilder
    {
        AltruistWebApplicationBuilder WebApp(Func<WebApplicationBuilder, WebApplicationBuilder>? setup = null);
        AltruistDatabaseBuilder NoCache();
        AltruistDatabaseBuilder SetupCache<TCacheConnectionSetup>(ICacheServiceToken token) where TCacheConnectionSetup : class, ICacheConnectionSetup<TCacheConnectionSetup>;
        AltruistDatabaseBuilder SetupCache<TCacheConnectionSetup>(ICacheServiceToken token, Func<TCacheConnectionSetup, TCacheConnectionSetup>? setup) where TCacheConnectionSetup : class, ICacheConnectionSetup<TCacheConnectionSetup>;
    }

    // Step 2: Choose Cache
    public class AltruistCacheBuilder : IAfterConnectionBuilder
    {
        protected readonly IServiceCollection Services;
        protected readonly IAltruistContext Settings;
        private string[] _args;

        internal AltruistCacheBuilder(IServiceCollection services, IAltruistContext settings, string[] args)
        {
            Services = services;
            Settings = settings;
            _args = args;
        }

        public AltruistWebApplicationBuilder WebApp(Func<WebApplicationBuilder, WebApplicationBuilder>? setup = null)
        {
            var app = WebApiHelper.Create(_args, Services);
            if (setup != null)
            {
                return new AltruistWebApplicationBuilder(setup(app), Settings);
            }
            return new AltruistWebApplicationBuilder(app, Settings);
        }

        public AltruistDatabaseBuilder NoCache()
        {
            return new AltruistDatabaseBuilder(Services, Settings, _args);
        }

        public AltruistDatabaseBuilder SetupCache<TCacheConnectionSetup>(ICacheServiceToken token) where TCacheConnectionSetup : class, ICacheConnectionSetup<TCacheConnectionSetup>
        {
            token.Configuration.Configure(Services);
            var setupInstance = Services.BuildServiceProvider()
                .GetRequiredService<TCacheConnectionSetup>();
            SetupCache(token, setupInstance);
            return new AltruistDatabaseBuilder(Services, Settings, _args);
        }

        public AltruistDatabaseBuilder SetupCache<TCacheConnectionSetup>(ICacheServiceToken token, Func<TCacheConnectionSetup, TCacheConnectionSetup>? setup) where TCacheConnectionSetup : class, ICacheConnectionSetup<TCacheConnectionSetup>
        {
            var serviceCollection = Services.AddSingleton<TCacheConnectionSetup>();
            var setupInstance = serviceCollection.BuildServiceProvider().GetService<TCacheConnectionSetup>();

            if (setup != null)
            {
                setupInstance = setup(setupInstance!);
            }

            SetupCache(token, setupInstance!);
            return new AltruistDatabaseBuilder(Services, Settings, _args);
        }

        private void SetupCache<TCacheConnectionSetup>(ICacheServiceToken token, TCacheConnectionSetup instance) where TCacheConnectionSetup : class, ICacheConnectionSetup<TCacheConnectionSetup>
        {
            token.Configuration.Configure(Services);
            Services.AddSingleton<TCacheConnectionSetup>();
            instance.Build(Settings);
            // readding the built instance
            Services.AddSingleton(instance);
            Settings.CacheToken = token;
        }
    }

    // Step 3: Choose Database
    public class AltruistDatabaseBuilder
    {
        protected readonly IServiceCollection Services;
        protected readonly IAltruistContext Settings;
        private string[] _args;

        internal AltruistDatabaseBuilder(IServiceCollection services, IAltruistContext settings, string[] args)
        {
            Services = services;
            Settings = settings;
            _args = args;
        }

        public AltruistWebApplicationBuilder WebApp(Func<WebApplicationBuilder, WebApplicationBuilder>? setup = null)
        {
            var app = WebApiHelper.Create(_args, Services);
            if (setup != null)
            {
                return new AltruistWebApplicationBuilder(setup(app), Settings);
            }
            return new AltruistWebApplicationBuilder(app, Settings);
        }


        public AltruistApplicationBuilder NoDatabase()
        {
            return new AltruistApplicationBuilder(Services, Settings, _args);
        }

        public AltruistApplicationBuilder SetupDatabase<TDatabaseConnectionSetup>(IDatabaseServiceToken token, Func<TDatabaseConnectionSetup, TDatabaseConnectionSetup>? setup = null) where TDatabaseConnectionSetup : class, IDatabaseConnectionSetup<TDatabaseConnectionSetup>
        {
            var serviceCollection = Services.AddSingleton<TDatabaseConnectionSetup>();
            var setupInstance = serviceCollection.BuildServiceProvider().GetService<TDatabaseConnectionSetup>();

            if (setup != null)
            {
                setupInstance = setup(setupInstance!);
            }

            SetupDatabase(token, setupInstance!);
            return new AltruistApplicationBuilder(Services, Settings, _args);
        }

        private void SetupDatabase<TDatabaseConnectionSetup>(IDatabaseServiceToken token, TDatabaseConnectionSetup instance) where TDatabaseConnectionSetup : class, IDatabaseConnectionSetup<TDatabaseConnectionSetup>
        {
            token.Configuration.Configure(Services);
            Services.AddSingleton<TDatabaseConnectionSetup>();
            instance.Build(Settings);
            // readding the built instance
            Services.AddSingleton(instance);
            Settings.DatabaseTokens.Add(token);
        }
    }

    public class AltruistApplicationBuilder
    {
        protected readonly IServiceCollection Services;
        protected readonly IAltruistContext Settings;
        private string[] _args;

        public AltruistApplicationBuilder(IServiceCollection services, IAltruistContext settings, string[] args)
        {
            Services = services;
            Settings = settings;
            _args = args;
        }

        public AltruistApplicationBuilder Codec<TCodec>() where TCodec : class, ICodec
        {
            Services.AddSingleton<TCodec>();
            return this;
        }

        public AltruistWebServerBuilder WebApp(Func<WebApplicationBuilder, WebApplicationBuilder>? setup = null)
        {
            var app = WebApiHelper.Create(_args, Services);
            if (setup != null)
            {
                return new AltruistWebServerBuilder(setup(app), Settings);
            }
            return new AltruistWebServerBuilder(app, Settings);
        }
    }

    public class AltruistWebApplicationBuilder
    {
        protected readonly WebApplicationBuilder Builder;
        protected readonly IAltruistContext Settings;

        public AltruistWebApplicationBuilder(WebApplicationBuilder builder, IAltruistContext settings)
        {
            Builder = builder;
            Settings = settings;
        }

        public AppBuilder Configure(Func<WebApplication, WebApplication> setup) => new AppBuilder(setup!(Builder.Build()));

        public void StartServer()
        {
            new AltruistWebServerBuilder(Builder, Settings).StartServer();
        }
    }

    public class AltruistWebServerBuilder
    {
        private WebApplicationBuilder Builder { get; }
        private WebApplication? App { get; set; }
        public AltruistWebServerBuilder(WebApplicationBuilder builder, IAltruistContext altruistContext)
        {
            Builder = builder;
            altruistContext.Validate();
        }

        public AppBuilder Configure(Func<WebApplication, WebApplication> setup) => new AppBuilder(setup!(App!));

        public void StartServer()
        {
            StartServer("localhost", 8080);
        }

        public void StartServer(string host, int port)
        {
            BuildApp();
            new AppBuilder(App!).StartServer(host, port);
        }

        public AppBuilder BuildApp()
        {
            if (App == null)
            {
                Builder.Services.AddControllers();
                App = Builder
                .Build();

                var webSocketOptions = new WebSocketOptions
                {
                    KeepAliveInterval = TimeSpan.FromMinutes(2)
                };
                App.UseWebSockets(webSocketOptions);
                App.UseRouting();
                App.MapControllers();
            }

            return new AppBuilder(App!);
        }
    }

    public class AltruistEngineBuilder
    {
        private IServiceCollection Services { get; }
        private IAltruistContext Settings;
        private string[] _args;
        public AltruistEngineBuilder(IServiceCollection services, IAltruistContext Settings, string[] args)
        {
            Services = services;
            this.Settings = Settings;
            _args = args;
        }

        public AltruistConnectionBuilder NoEngine()
        {
            return new AltruistConnectionBuilder(Services, Settings, _args);
        }

        public AltruistConnectionBuilder EnableEngine(int hz, CycleUnit unit = CycleUnit.Ticks, int? throttle = null)
        {
            Services.AddSingleton<IAltruistEngineRouter, InMemoryEngineRouter>();
            Services.AddSingleton<EngineClientSender>();
            Services.AddSingleton<IAltruistEngine>(sp =>
            {
                var env = sp.GetRequiredService<IHostEnvironment>();
                var engine = new AltruistEngine(sp, hz, unit, throttle);
                engine.Enable();

                if (env.IsDevelopment())
                {
                    return new EngineWithDiagnostics(engine, sp.GetRequiredService<ILoggerFactory>());
                }
                else
                {
                    return engine;
                }
            });

            Services.AddSingleton<IAltruistRouter>(sp => sp.GetRequiredService<IAltruistEngineRouter>());
            Services.AddSingleton<MethodScheduler>();
            Settings.EngineEnabled = true;
            return new AltruistConnectionBuilder(Services, Settings, _args);
        }
    }

    public class AppBuilder
    {
        private readonly WebApplication _app;
        private readonly Dictionary<Type, string> _portals;

        public AppBuilder(WebApplication app)
        {
            _app = app;
            _portals = app.Services.GetService<ITransportConnectionSetupBase>()!.Portals.ToDictionary(x => x.Key, x => x.Value.Path);
        }

        public AppBuilder Configure(Func<WebApplication, WebApplication> setup)
        {
            if (setup != null)
            {
                return new AppBuilder(setup(_app));
            }
            return this;
        }

        public AppBuilder UseAuth()
        {
            _app.UseAuthentication();
            _app.UseAuthorization();
            return this;
        }

        public void StartServer()
        {
            StartServer("localhost", 8080);
        }

        public void StartServer(string host, int port)
        {
            _ = StartServer($"http://{host}:{port}");
        }


        public async Task Startup()
        {
            var _settings = _app.Services.GetRequiredService<IAltruistContext>();
            var actions = _app.Services.GetServices<IAction>();
            var appStatus = _app.Services.GetRequiredService<IAppStatus>();

            var dbServices = new List<IGeneralDatabaseProvider>();
            var dbTokens = _settings.DatabaseTokens;
            var cacheToken = _settings.CacheToken;
            var connectedServicesCount = 0;
            var totalServices = dbTokens.Count;
            var allServicesConnected = new TaskCompletionSource<bool>();

            foreach (var dbToken in dbTokens)
            {
                var dbService = _app.Services.GetServices<IGeneralDatabaseProvider>()
                    .Where(token => token.Token == dbToken)
                    .FirstOrDefault();

                if (dbService != null && !dbService.IsConnected)
                {
                    dbService.OnConnected += () =>
                    {
                        connectedServicesCount++;
                        if (connectedServicesCount == totalServices)
                        {
                            allServicesConnected.SetResult(true);
                        }
                    };

                    dbService.OnFailed += (ex) => appStatus.SignalState(ReadyState.Failed);
                }
                else if (dbService != null)
                {
                    dbServices.Add(dbService);
                }
                else
                {
                    appStatus.SignalState(ReadyState.Failed);
                }
            }

            if (cacheToken != null)
            {
                var cacheService = _app.Services.GetServices<ICacheProvider>()
                    .Where(cache => cache.Token == cacheToken)
                    .FirstOrDefault();
                if (cacheService != null && !cacheService.IsConnected)
                {
                    cacheService.OnConnected += () =>
                    {
                        connectedServicesCount++;
                        if (connectedServicesCount == totalServices)
                        {
                            allServicesConnected.SetResult(true);
                        }
                    };

                    cacheService.OnFailed += (ex) => appStatus.SignalState(ReadyState.Failed);
                }
                else
                {
                    appStatus.SignalState(ReadyState.Failed);
                }
            }

            await allServicesConnected.Task;

            if (appStatus.Status == ReadyState.Starting)
            {
                foreach (var action in actions)
                {
                    await action.Run();
                }

                appStatus.SignalState(ReadyState.Alive);
            }
        }

        public async Task StartServer(string connectionString)
        {
            EventHandlerRegistry<IPortal>.ScanAndRegisterHandlers(_app.Services);

            var _settings = _app.Services.GetRequiredService<IAltruistContext>();
            var gatewayServices = _app.Services.GetServices<IRelayService>();
            var splitted = connectionString.Split(':');
            var protocol = splitted[0];
            var host = splitted[1].Replace("//", "");
            var port = int.Parse(splitted[2]);
            _settings.ServerInfo = new ServerInfo("Altruist Websocket Server", "ws", host, port);

            var frameLine = new string('═', 80);
            var PortaledText = (string text) => $"{text}".PadLeft((80 + text.Length) / 2).PadRight(80);

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
            var transport = _app.Services.GetService<ITransport>();

            foreach (var (type, path) in _portals)
            {
                logBuilder.AppendLine(PortaledText($"🔌 Opening {type.Name} through {path}"));
                transport!.UseTransportEndpoints(_app, type, path);
            }

            foreach (var service in gatewayServices)
            {
                logBuilder.AppendLine(PortaledText($"🔗 Starting relay portal {service.GetType().Name}..."));

                try
                {
                    _ = Task.Run(service.ConnectAsync);
                }
                catch (Exception ex)
                {
                    logBuilder.AppendLine(PortaledText($"❌ Connection failed for {service.GetType().Name}: {ex.Message}"));
                }
            }

            await Startup();

            var settingsLines = _settings.ToString()!.Replace('\r', ' ').Split('\n');

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

            if (_settings.EngineEnabled)
            {
                var scheduler = _app.Services.GetService<MethodScheduler>();
                var methods = scheduler!.RegisterMethods(_app.Services);
                var engine = _app.Services.GetService<IAltruistEngine>();
                engine!.Start();

                logBuilder.AppendLine(PortaledText(
                    $"⚡⚡ [ENGINE {engine.Rate}Hz] Unleashed — powerful, fast, and breaking speed limits!"));

                if (methods.Any())
                {
                    var methodsDisplay = string.Join("\n", methods.Select(m =>
                    {
                        var regen = m.GetCustomAttribute<CycleAttribute>();
                        var frequency = regen!.ToString();
                        return $"       ↳ {m.DeclaringType?.FullName!.Split('`')[0]}.{m.Name} ({frequency})";
                    }));

                    logBuilder.AppendLine(PortaledText($"   🚀 Scheduled methods:\n{methodsDisplay}"));
                }
                else
                {
                    logBuilder.AppendLine(PortaledText("❗Nothing to run.. 🙁 Mark something with [Regen(Hz or cron)] to let me show my power. Please!"));
                }
            }

            Console.WriteLine("\n" + logBuilder.ToString() + "\n");
            _app.Run(connectionString);
        }
    }
}
