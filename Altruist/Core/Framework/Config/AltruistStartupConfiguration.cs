
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Altruist.Transport;
using Altruist.Contracts;
using Altruist.Web.Features;

namespace Altruist
{
    [Configuration(order: int.MaxValue)]
    public sealed class AltruistStartupConfiguration : IAltruistConfiguration
    {
        private readonly ApplicationArgs _args;
        private readonly string _host;
        private readonly string _port;
        private readonly string _transportType;

        public AltruistStartupConfiguration(
            ApplicationArgs args,
            [ConfigValue("altruist:server:host", "localhost")] string host,
            [ConfigValue("altruist:server:port", "8080")] string port,
            [ConfigValue("altruist:server:transport", "websocket")] string transportType,

            IServiceCollection rootServices,
            ILoggerFactory loggerFactory,
            IAltruistContext settings,
            IServerStatus appStatus,
            ITransport? transport = null
        )
        {
            _args = args;
            _host = host;
            _port = port;
            _transportType = transportType;

            if (!string.Equals(_transportType, "websocket", StringComparison.OrdinalIgnoreCase))
                return;

            var builder = WebApplication.CreateBuilder(_args?.Args ?? Array.Empty<string>());
            builder.Logging.ClearProviders();
            foreach (var d in rootServices) builder.Services.Add(d);

            builder.Services.AddControllers();
            var app = builder.Build();

            var webSocketOptions = new WebSocketOptions { KeepAliveInterval = TimeSpan.FromMinutes(2) };
            app.UseWebSockets(webSocketOptions);
            app.UseRouting();
            app.MapControllers();
            app.UseMiddleware<ReadinessMiddleware>();

            settings.AppStatus = appStatus;

            var portals = PortalDiscovery.Discover().Distinct().ToArray();

            if (!int.TryParse(_port, out var portNum)) portNum = 8080;
            settings.ServerInfo = new ServerInfo("Altruist Websocket Server", "ws", _host, portNum);

            var logger = loggerFactory.CreateLogger<AltruistStartupConfiguration>();
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

            foreach (var (type, path) in portals)
            {
                logBuilder.AppendLine(PortaledText($"🔌 Opening {type.Name} through {path}"));
                transport?.UseTransportEndpoints(app, type, path);
            }

            transport?.RouteTraffic(app);

            // Settings summary block (unchanged)
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

            Console.WriteLine("\n" + logBuilder + "\n");
            Console.WriteLine(settings.AppStatus.ToString());

            if (settings.AppStatus.Status != ReadyState.Alive)
            {
                logger.LogWarning("🕒 All systems initialized, but I'm still waiting for a few lazy services to show up. Hang tight — no inbound or outbound messages are allowed yet. And if you enabled the engine... nope, not starting that until everyone's here!");
            }

            // Scan & wire any event handlers (safe to pass IServiceProvider)
            EventHandlerRegistry<IPortal>.ScanAndRegisterHandlers(app.Services);

            // Run the server (blocking)
            var connectionString = $"http://{_host}:{portNum}";
            app.Run(connectionString);
        }

        // Required by IAltruistConfiguration. All work happens in the ctor now.
        public Task Configure(IServiceCollection services) => Task.CompletedTask;
    }
}
