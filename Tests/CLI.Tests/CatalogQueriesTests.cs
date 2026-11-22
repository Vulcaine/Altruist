using Altruist.CLI.Catalog;

namespace Altruist.CLI.Tests;

public class CatalogQueriesTests
{
    private static readonly CapabilityCatalog Catalog = new("1.0.0",
    [
        Capability("transports", "Transports and connections", "Accepts TCP and WebSocket clients.",
            config: ["altruist:server:transport:websocket:codec:provider", "altruist:server:transport:websocket:enabled"],
            types: ["Altruist.Web.WebSocketTransport"]),
        Capability("http-api", "HTTP API", "Host, port and path of the HTTP app.", config: ["altruist:server:http:port"]),
        Capability("dotnet-client", ".NET Client", "Talks to a server from .NET.", types: ["Altruist.Client.AltruistWebSocketClient"]),
        Capability("rooms", "Match rooms", "Short-lived authoritative matches."),
    ]);

    [Fact]
    public void finds_a_capability_by_name_ignoring_case()
    {
        var lookup = CatalogQueries.Find(Catalog, "Rooms");

        Assert.Equal("rooms", Assert.IsType<CapabilityLookup.Found>(lookup).Capability.Spec.Name);
    }

    [Fact]
    public void suggests_names_sharing_a_word_when_the_name_is_unknown()
    {
        var lookup = CatalogQueries.Find(Catalog, "room");

        Assert.Equal(["rooms"], Assert.IsType<CapabilityLookup.NotFound>(lookup).Suggestions);
    }

    [Fact]
    public void search_by_config_key_returns_its_capability_and_the_key()
    {
        var hit = Assert.Single(CatalogQueries.Search(Catalog, "altruist:server:http:port", limit: 8));

        Assert.Equal("http-api", hit.Capability.Spec.Name);
        Assert.Equal(["altruist:server:http:port"], hit.Matches);
    }

    [Fact]
    public void search_ranks_the_capability_with_more_matching_members_first()
    {
        var hits = CatalogQueries.Search(Catalog, "websocket", limit: 8);

        Assert.Equal(["transports", "dotnet-client"], hits.Select(hit => hit.Capability.Spec.Name));
    }

    [Fact]
    public void search_ignores_single_letters()
    {
        Assert.Empty(CatalogQueries.Search(Catalog, "a e", limit: 8));
    }

    [Fact]
    public void search_returns_at_most_the_limit()
    {
        Assert.Single(CatalogQueries.Search(Catalog, "websocket", limit: 1));
    }

    [Fact]
    public void the_embedded_catalog_loads_with_every_capability_named()
    {
        var catalog = CatalogSource.FromEmbeddedResource();

        Assert.NotEmpty(catalog.Capabilities);
        Assert.All(catalog.Capabilities, capability => Assert.False(string.IsNullOrWhiteSpace(capability.Spec.Name)));
    }

    private static Capability Capability(
        string name, string title, string summary, IReadOnlyList<string>? config = null, IReadOnlyList<string>? types = null) =>
        new(
            new CapabilitySpec(name, title, summary, Use: [], Avoid: [], Packages: [], Docs: [], Example: null),
            KeyTypes: (types ?? []).Select(type => new TypeEntry(type, Summary: null)).ToList(),
            Services: [],
            Config: (config ?? []).Select(key => new ConfigEntry(key, Type: null, Default: null, Description: null, Live: false, Gates: [])).ToList(),
            Attributes: []);
}
