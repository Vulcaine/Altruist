using System.ComponentModel;
using Altruist.CLI.Catalog;
using ModelContextProtocol.Server;

namespace Altruist.CLI.Mcp;

/// <summary>The capability catalog as MCP tools: what Altruist already provides, so an agent uses it instead of writing its own.</summary>
[McpServerToolType]
public sealed class KnowledgeTools(CapabilityCatalog catalog)
{
    private const int SearchLimit = 8;

    [McpServerTool(Name = "altruist_capabilities", ReadOnly = true, Idempotent = true)]
    [Description("Lists every system of the Altruist game-server framework (networking, persistence, auth, rooms, worlds, physics, clients and more), one line each. Call it first when you plan Altruist server code.")]
    public string ListCapabilities() => CatalogText.Index(catalog);

    [McpServerTool(Name = "altruist_capability", ReadOnly = true, Idempotent = true)]
    [Description("Everything about one Altruist system: what it is for, when to use something else, the packages, docs, a working example, every config key with type and default, and its types, services and attributes.")]
    public string GetCapability(
        [Description("The capability name from altruist_capabilities or altruist_search, for example rooms or transports.")] string name) =>
        CatalogText.Lookup(CatalogQueries.Find(catalog, name));

    [McpServerTool(Name = "altruist_search", ReadOnly = true, Idempotent = true)]
    [Description("Finds the Altruist systems for a task or a name: words such as \"websocket login\", a config key such as altruist:server:http:port, a type such as RoomHost, or an attribute such as Portal.")]
    public string Search(
        [Description("Words, a config key, a type or an attribute name.")] string query) =>
        CatalogText.SearchResults(query, CatalogQueries.Search(catalog, query, SearchLimit));
}
