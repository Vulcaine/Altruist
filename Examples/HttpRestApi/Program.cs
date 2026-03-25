using Altruist;

/// <summary>
/// Minimal HTTP REST API server — no sockets, no gaming engine.
/// Demonstrates: [HttpController], DI, config binding.
/// Run: dotnet run
/// Test: curl http://localhost:8080/api/health
///       curl -X POST http://localhost:8080/api/items -d '{"name":"Sword","value":100}'
/// </summary>
public static class Program
{
    public static async Task Main(string[] args)
        => await AltruistApplication.Run(args);
}
