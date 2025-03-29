using System.Reflection;
using System.Text;
using Altruist.Contracts;
using Altruist.InMemory;
using Altruist.Transport;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Altruist
{
    public class AltruistBuilder
    {
        private WebApplicationBuilder Builder { get; }
        public IAltruistContext Settings { get; } = new AltruistServerContext();

        private AltruistBuilder(string[] args)
        {
            Builder = WebApplication.CreateBuilder(args);
            Builder.Logging.ClearProviders();

            var frameworkVersion = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "1";
            Builder.Logging.AddProvider(new AltruistLoggerProvider(frameworkVersion));

            // Add core services
            Builder.Services.AddSingleton(Builder.Services);
            Builder.Services.AddSingleton(Settings);
            Builder.Services.AddSingleton<ClientSender>();
            Builder.Services.AddSingleton<EngineClientSender>();
            Builder.Services.AddSingleton<RoomSender>();
            Builder.Services.AddSingleton<BroadcastSender>();
            Builder.Services.AddSingleton<ClientSynchronizator>();
            // Setup cache
            Builder.Services.AddSingleton<InMemoryCache>();
            Builder.Services.AddSingleton<IMemoryCache>(sp => sp.GetRequiredService<InMemoryCache>());
            Builder.Services.AddSingleton<ICache>(sp => sp.GetRequiredService<InMemoryCache>());

            Builder.Services.AddSingleton<IAltruistRouter, InMemoryDirectRouter>();
            Builder.Services.AddSingleton<IAltruistEngineRouter, InMemoryEngineRouter>();
            Builder.Services.AddSingleton<IMessageDecoder, JsonMessageDecoder>();
            Builder.Services.AddSingleton<IMessageEncoder, JsonMessageEncoder>();
            Builder.Services.AddSingleton<IConnectionStore, InMemoryConnectionStore>();
            Builder.Services.AddSingleton(typeof(IPlayerService<>), typeof(InMemoryPlayerService<>));
            Builder.Services.AddSingleton<IPortalContext, PortalContext>();
            Builder.Services.AddSingleton(typeof(IPlayerService<>), typeof(InMemoryPlayerService<>));
        }

        public static AltruistConnectionBuilder Create(string[] args) => new AltruistBuilder(args).ToConnectionBuilder();

        private AltruistConnectionBuilder ToConnectionBuilder() => new AltruistConnectionBuilder(Builder, Settings);
    }

    // Step 1: Choose Transport
    public class AltruistConnectionBuilder
    {
        protected readonly WebApplicationBuilder Builder;
        protected readonly IAltruistContext Settings;

        internal AltruistConnectionBuilder(WebApplicationBuilder builder, IAltruistContext settings)
        {
            Builder = builder;
            Settings = settings;
        }

        public AltruistCacheBuilder SetupTransport<TTransportConnectionSetup>(ITransportServiceToken token, Func<TTransportConnectionSetup, TTransportConnectionSetup> setup) where TTransportConnectionSetup : class, ITransportConnectionSetup<TTransportConnectionSetup>
        {
            var serviceCollection = Builder.Services.AddSingleton<TTransportConnectionSetup>();
            var setupInstance = serviceCollection.BuildServiceProvider().GetService<TTransportConnectionSetup>();

            if (setup != null)
            {
                setupInstance = setup(setupInstance!);
            }

            SetupTransport(token, setupInstance!);
            Settings.TransportToken = token;
            return new AltruistCacheBuilder(Builder, Settings);
        }

        private void SetupTransport<TTransportConnectionSetup>(ITransportServiceToken token, TTransportConnectionSetup instance) where TTransportConnectionSetup : class, ITransportConnectionSetup<TTransportConnectionSetup>
        {
            token.Configuration.Configure(Builder.Services);
            Builder.Services.AddSingleton(token);
            instance.Build();
            // readding the built instance
            Builder.Services.AddSingleton(instance);
        }

        public void SetupTransport<TTransportConnectionSetup>(ITransportServiceToken token) where TTransportConnectionSetup : class, ITransportConnectionSetup<TTransportConnectionSetup>
        {
            token.Configuration.Configure(Builder.Services);
            var setupInstance = Builder.Services.BuildServiceProvider()
                .GetRequiredService<TTransportConnectionSetup>();
            SetupTransport(token, setupInstance);
        }
    }

    // Step 2: Choose Cache
    public class AltruistCacheBuilder
    {
        protected readonly WebApplicationBuilder Builder;
        protected readonly IAltruistContext Settings;

        internal AltruistCacheBuilder(WebApplicationBuilder builder, IAltruistContext settings)
        {
            Builder = builder;
            Settings = settings;
        }

        public void StartServer()
        {
            new AltruistServerBuilder(Builder, Settings).StartServer();
        }

        public AltruistDatabaseBuilder NoCache()
        {
            return new AltruistDatabaseBuilder(Builder, Settings);
        }

        public AltruistDatabaseBuilder SetupCache<TCacheConnectionSetup>(ICacheServiceToken token) where TCacheConnectionSetup : class, ICacheConnectionSetup<TCacheConnectionSetup>
        {
            token.Configuration.Configure(Builder.Services);
            var setupInstance = Builder.Services.BuildServiceProvider()
                .GetRequiredService<TCacheConnectionSetup>();
            SetupCache(token, setupInstance);
            return new AltruistDatabaseBuilder(Builder, Settings);
        }

        public AltruistDatabaseBuilder SetupCache<TCacheConnectionSetup>(ICacheServiceToken token, Func<TCacheConnectionSetup, TCacheConnectionSetup> setup) where TCacheConnectionSetup : class, ICacheConnectionSetup<TCacheConnectionSetup>
        {
            var serviceCollection = Builder.Services.AddSingleton<TCacheConnectionSetup>();
            var setupInstance = serviceCollection.BuildServiceProvider().GetService<TCacheConnectionSetup>();

            if (setup != null)
            {
                setupInstance = setup(setupInstance!);
            }

            SetupCache(token, setupInstance!);

            return new AltruistDatabaseBuilder(Builder, Settings);
        }

        private void SetupCache<TCacheConnectionSetup>(ICacheServiceToken token, TCacheConnectionSetup instance) where TCacheConnectionSetup : class, ICacheConnectionSetup<TCacheConnectionSetup>
        {
            token.Configuration.Configure(Builder.Services);
            Builder.Services.AddSingleton<TCacheConnectionSetup>();
            Builder.Services.AddSingleton(token);
            instance.Build();
            // readding the built instance
            Builder.Services.AddSingleton(instance);
        }
    }

    // Step 3: Choose Database
    public class AltruistDatabaseBuilder
    {
        protected readonly WebApplicationBuilder Builder;
        protected readonly IAltruistContext Settings;

        internal AltruistDatabaseBuilder(WebApplicationBuilder builder, IAltruistContext settings)
        {
            Builder = builder;
            Settings = settings;
        }

        public AltruistServerBuilder NoDatabase()
        {
            return new AltruistServerBuilder(Builder, Settings);
        }

        public AltruistServerBuilder SetupDatabase<TDatabaseConnectionSetup>(IDatabaseServiceToken token, Func<TDatabaseConnectionSetup, TDatabaseConnectionSetup> setup) where TDatabaseConnectionSetup : class, IDatabaseConnectionSetup<TDatabaseConnectionSetup>
        {
            token.Configuration.Configure(Builder.Services);
            Builder.Services.AddSingleton<TDatabaseConnectionSetup>();
            setup(Builder.Services.BuildServiceProvider()
                .GetRequiredService<TDatabaseConnectionSetup>()).Build();
            Builder.Services.AddSingleton(token);
            return new AltruistServerBuilder(Builder, Settings);
        }
    }

    public class AltruistServerBuilder
    {
        private WebApplicationBuilder Builder { get; }
        private WebApplication? App { get; set; }
        public AltruistServerBuilder(WebApplicationBuilder webApplicationBuilder, IAltruistContext altruistContext)
        {
            Builder = webApplicationBuilder;
            altruistContext.Validate();
        }

        public void StartServer()
        {
            StartServer("localhost", 8080);
        }

        public void StartServer(string host, int port)
        {
            EnsureAppBuilt();
            new AppBuilder(App!).StartServer(host, port);
        }

        private void EnsureAppBuilt()
        {
            if (App == null)
            {
                App = Builder
                .Build();

                var webSocketOptions = new WebSocketOptions
                {
                    KeepAliveInterval = TimeSpan.FromMinutes(2)
                };
                App.UseWebSockets(webSocketOptions);
                App.UseRouting();
            }
        }
    }

    public class AltruistEngineBuilder
    {
        private WebApplicationBuilder Builder { get; }
        private IAltruistContext Settings;
        public AltruistEngineBuilder(WebApplicationBuilder webApplicationBuilder, IAltruistContext Settings)
        {
            Builder = webApplicationBuilder;
            this.Settings = Settings;
        }

        public AltruistServerBuilder NoEngine()
        {
            return new AltruistServerBuilder(Builder, Settings);
        }

        public AltruistServerBuilder EnableEngine(int hz, CycleUnit unit = CycleUnit.Ticks, int? throttle = null)
        {
            Builder.Services.AddSingleton<IAltruistEngine>(sp =>
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

            Builder.Services.AddSingleton<IAltruistRouter>(sp => sp.GetRequiredService<IAltruistEngineRouter>());
            Builder.Services.AddSingleton<MethodScheduler>();
            Settings.EngineEnabled = true;
            return new AltruistServerBuilder(Builder, Settings);
        }
    }

    public class AppBuilder
    {
        private readonly WebApplication _app;
        private readonly Dictionary<Type, string> _portals;

        public AppBuilder(WebApplication app)
        {
            _app = app;
            _portals = app.Services.GetService<ITransportConnectionSetupBase>()!.Portals;
        }

        public void StartServer(string host, int port)
        {
            StartServer($"http://{host}:{port}");
        }

        public void StartServer(string connectionString)
        {
            EventHandlerRegistry<IPortal>.ScanAndRegisterHandlers(_app.Services);
            // EventHandlerRegistry<ITemple>.ScanAndRegisterHandlers(_app.Services);

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

            var settingsLines = _settings.ToString()!.Replace('\r', ' ').Split('\n');

            int lineWidth = 50;

            logBuilder.AppendLine("╔════════════════════════════════════════════════════╗");
            logBuilder.AppendLine("║ The Portals Are Open! Connect At:                  ║");

            foreach (var line in settingsLines)
            {
                int currentLineLength = "║ ".Length + line.Length + " ║".Length;
                string paddedLine = $"║ {line.PadRight(currentLineLength + (lineWidth - currentLineLength))} ║";
                logBuilder.AppendLine(paddedLine);
            }

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
