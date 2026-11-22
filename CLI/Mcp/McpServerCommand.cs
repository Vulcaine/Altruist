using Altruist.CLI.Catalog;
using Altruist.CLI.Runtime;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;

namespace Altruist.CLI.Mcp;

/// <summary><c>altruist mcp</c>: an MCP server on stdin/stdout for coding agents working on an Altruist server.</summary>
public static class McpServerCommand
{
    private const string Instructions =
        "Altruist is a .NET game-server framework. It already provides transports (WebSocket, TCP, UDP), portals and packet "
        + "routing, auth and sessions, persistence (vaults on Postgres, Redis cache), rooms, matchmaking, lobbies, worlds, "
        + "physics, AI and typed clients. Before writing server code in an Altruist project, find the system that does it "
        + "with altruist_search or altruist_capabilities, read it with altruist_capability, and follow its example and config "
        + "keys instead of writing your own handler, repository or auth. To start a new server project, run "
        + "`altruist create <Name>`. While the server runs with altruist:dashboard:enabled: true, altruist_runtime and the "
        + "other altruist_runtime_* tools read its config, connections, rooms, worlds, stored data and traffic; use them to "
        + "check what the code you wrote does instead of guessing. The tools that change the running server (drain, disconnect, "
        + "close a room, set config, save or remove a cache entry) ask the user to approve each change first.";

    // A dashboard view is built from in-memory state; one that takes longer is a server in trouble.
    private static readonly TimeSpan DashboardTimeout = TimeSpan.FromSeconds(10);

    public static async Task<int> RunAsync(string version)
    {
        var catalog = CatalogSource.FromEmbeddedResource();

        // An empty builder: the server must not pick up appsettings.json or DOTNET_ variables from the project it runs in.
        var builder = Host.CreateEmptyApplicationBuilder(settings: null);
        // stdout carries the protocol; anything else written there breaks the client.
        builder.Logging.AddConsole(options => options.LogToStandardErrorThreshold = LogLevel.Trace);
        builder.Services.AddSingleton(catalog);
        builder.Services.AddSingleton(DashboardEndpoint.FromEnvironment(Environment.GetEnvironmentVariable));
        builder.Services.AddSingleton(new HttpClient { Timeout = DashboardTimeout });
        builder.Services.AddSingleton<DashboardClient>();
        builder.Services.AddSingleton<RuntimeChanges>();
        builder.Services
            .AddMcpServer(options =>
            {
                options.ServerInfo = new Implementation { Name = "altruist", Version = version };
                options.ServerInstructions = Instructions;
            })
            .WithStdioServerTransport()
            .WithTools<KnowledgeTools>()
            .WithTools<RuntimeTools>()
            .WithTools<RuntimeChangeTools>();

        await builder.Build().RunAsync();
        return 0;
    }
}
