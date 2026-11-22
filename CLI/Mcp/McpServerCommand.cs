using Altruist.CLI.Catalog;
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
        + "keys instead of writing your own handler, repository or auth.";

    public static async Task<int> RunAsync(string version)
    {
        var catalog = CatalogSource.FromEmbeddedResource();

        // An empty builder: the server must not pick up appsettings.json or DOTNET_ variables from the project it runs in.
        var builder = Host.CreateEmptyApplicationBuilder(settings: null);
        // stdout carries the protocol; anything else written there breaks the client.
        builder.Logging.AddConsole(options => options.LogToStandardErrorThreshold = LogLevel.Trace);
        builder.Services.AddSingleton(catalog);
        builder.Services
            .AddMcpServer(options =>
            {
                options.ServerInfo = new Implementation { Name = "altruist", Version = version };
                options.ServerInstructions = Instructions;
            })
            .WithStdioServerTransport()
            .WithTools<KnowledgeTools>();

        await builder.Build().RunAsync();
        return 0;
    }
}
