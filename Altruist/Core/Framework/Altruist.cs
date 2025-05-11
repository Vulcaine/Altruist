/* 
Copyright 2025 Aron Gere

Licensed under the Apache License, Version 2.0 (the "License");
you may not use this file except in compliance with the License.
You may obtain a copy of the License at

    http://www.apache.org/licenses/LICENSE-2.0

Unless required by applicable law or agreed to in writing, software
distributed under the License is distributed on an "AS IS" BASIS,
WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
See the License for the specific language governing permissions and
limitations under the License.
*/

using System.Reflection;
using System.Text;
using Altruist.Contracts;
using Altruist.Transport;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
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

    public class AltruistWebServerBuilder
    {
        private WebApplicationBuilder Builder { get; }
        private WebApplication? App { get; set; }
        public AltruistWebServerBuilder(WebApplicationBuilder builder, IAltruistContext altruistContext)
        {
            Builder = builder;
            altruistContext.Validate();
        }

        public AppManager Configure(Func<WebApplication, WebApplication> setup) => new AppManager(setup!(App!));

        public void StartServer()
        {
            StartServer("localhost", 8080);
        }

        public void StartServer(string host, int port)
        {
            BuildApp();
            new AppManager(App!).StartServer(host, port);
        }

        public AppManager BuildApp()
        {
            if (App == null)
            {
                Builder.Services.AddControllers();
                ServiceConfig.Configure(Builder.Services);
                App = Builder
                       .Build();

                var webSocketOptions = new WebSocketOptions
                {
                    KeepAliveInterval = TimeSpan.FromMinutes(2)
                };
                App.UseWebSockets(webSocketOptions);
                App.UseRouting();
                App.MapControllers();
                App.UseMiddleware<ReadinessMiddleware>();
            }

            return new AppManager(App!);
        }
    }

    public class AppManager
    {
        public readonly WebApplication App;
        private readonly Dictionary<Type, string> _portals;

        public readonly IServerStatus AppState;

        public AppManager(WebApplication app)
        {
            App = app;
            AppState = app.Services.GetRequiredService<IServerStatus>();
            var settings = app.Services.GetRequiredService<IAltruistContext>();
            settings.AppStatus = AppState;
            _portals = app.Services.GetService<ITransportConnectionSetupBase>()!.Portals.ToDictionary(x => x.Key, x => x.Value.Path);
        }

        public IServiceProvider ServiceProvider => App.Services;

        public AppManager Configure(Func<WebApplication, WebApplication> setup)
        {
            if (setup != null)
            {
                return new AppManager(setup(App));
            }
            return this;
        }

        public AppManager UseAuth()
        {
            App.UseAuthentication();
            App.UseAuthorization();
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

        public void Shutdown(Exception? ex = null, string reason = "")
        {
            var settings = App.Services.GetRequiredService<IAltruistContext>();
            var appStatus = settings.AppStatus;
            var logger = App.Services.GetRequiredService<ILoggerFactory>().CreateLogger<AppManager>();
            if (ex != null)
            {
                logger.LogCritical($"❌ {reason}, {ex.Message}.");
            }
            else
            {
                logger.LogInformation($"{reason}.");
            }

            appStatus.SignalState(ReadyState.Failed);
            Environment.Exit(1);
        }

        public async Task Startup()
        {
            var settings = App.Services.GetRequiredService<IAltruistContext>();
            var appStatus = settings.AppStatus;
            await appStatus.StartupAsync(this);
        }

        public Task StartServer(string connectionString)
        {
            EventHandlerRegistry<IPortal>.ScanAndRegisterHandlers(App.Services);
            var logger = App.Services.GetRequiredService<ILoggerFactory>().CreateLogger<AppManager>();
            var _settings = App.Services.GetRequiredService<IAltruistContext>();
            var splitted = connectionString.Split(':');
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
            var transport = App.Services.GetService<ITransport>();

            foreach (var (type, path) in _portals)
            {
                logBuilder.AppendLine(PortaledText($"🔌 Opening {type.Name} through {path}"));
                transport!.UseTransportEndpoints(App, type, path);
            }

            transport!.RouteTraffic(App);

            _ = Startup();

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

            Console.WriteLine("\n" + logBuilder.ToString() + "\n");
            Console.WriteLine(_settings.AppStatus.ToString());

            if (_settings.AppStatus.Status != ReadyState.Alive)
            {
                logger.LogWarning("🕒 All systems initialized, but I'm still waiting for a few lazy services to show up. Hang tight — no inbound or outbound messages are allowed yet. And if you enabled the engine... nope, not starting that until everyone's here!");

            }

            App.Run(connectionString);
            return Task.CompletedTask;
        }
    }
}
