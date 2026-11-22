using Altruist.CLI.Catalog;

namespace Altruist.CLI.Tests;

public class CatalogTextTests
{
    private static readonly Capability Http = new(
        new CapabilitySpec("http-api", "HTTP API", "Serves HTTP. Also more.", Use: ["When you need REST."], Avoid: [],
            Packages: ["Altruist.Web"], Docs: ["/docs/networking/http"], Example: "```yaml\naltruist: {}\n```"),
        KeyTypes: [],
        Services: [],
        Config: [new ConfigEntry("altruist:server:http:port", "int", "8080", "Listening port.", Live: false, Gates: [])],
        Attributes: []);

    [Fact]
    public void a_capability_shows_each_config_key_with_type_and_default()
    {
        var text = CatalogText.Capability(Http);

        Assert.Contains("- `altruist:server:http:port` (int, default `8080`): Listening port.", text);
    }

    [Fact]
    public void docs_paths_become_links_to_the_docs_site()
    {
        Assert.Contains("https://altruist-docs.vercel.app/docs/networking/http", CatalogText.Capability(Http));
    }

    [Fact]
    public void the_index_shows_only_the_first_sentence_of_a_summary()
    {
        var text = CatalogText.Index(new CapabilityCatalog("1.0.0", [Http]));

        Assert.Contains("- `http-api`: HTTP API. Serves HTTP.\n", text);
        Assert.DoesNotContain("Also more.", text);
    }

    [Fact]
    public void an_unknown_name_with_no_close_match_points_to_the_list_and_search()
    {
        var text = CatalogText.Lookup(new CapabilityLookup.NotFound("zzz", []));

        Assert.Contains("altruist_capabilities", text);
        Assert.Contains("altruist_search", text);
    }
}
