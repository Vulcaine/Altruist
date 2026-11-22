using System.Text;

namespace Altruist.CLI.Catalog;

/// <summary>The catalog as Markdown for a coding agent: short enough to read whole, complete enough to write code from.</summary>
public static class CatalogText
{
    private const string DocsSite = "https://altruist-docs.vercel.app";

    public static string Index(CapabilityCatalog catalog)
    {
        var text = new StringBuilder($"# Altruist {catalog.Version} capabilities\n\n");
        text.Append("Each line is `name`: title, what it is for. Read one with altruist_capability.\n\n");
        foreach (var capability in catalog.Capabilities)
            text.Append($"- `{capability.Spec.Name}`: {capability.Spec.Title}. {FirstSentence(capability.Spec.Summary)}\n");
        return text.ToString();
    }

    public static string Lookup(CapabilityLookup lookup) => lookup switch
    {
        CapabilityLookup.Found found => Capability(found.Capability),
        CapabilityLookup.NotFound { Suggestions.Count: > 0 } missing =>
            $"No capability is named `{missing.Name}`. Close names: {string.Join(", ", missing.Suggestions.Select(s => $"`{s}`"))}.",
        CapabilityLookup.NotFound missing =>
            $"No capability is named `{missing.Name}`. List them with altruist_capabilities or search with altruist_search.",
        _ => throw new ArgumentOutOfRangeException(nameof(lookup)),
    };

    public static string Capability(Capability capability)
    {
        var spec = capability.Spec;
        var text = new StringBuilder($"# {spec.Title} (`{spec.Name}`)\n\n{spec.Summary}\n");
        AppendList(text, "Use it when", spec.Use);
        AppendList(text, "Don't use it when", spec.Avoid);
        if (spec.Packages.Count > 0) text.Append($"\n**Packages:** {string.Join(", ", spec.Packages.Select(p => $"`{p}`"))}\n");
        if (spec.Docs.Count > 0) text.Append($"\n**Docs:** {string.Join(", ", spec.Docs.Select(DocLink))}\n");
        if (spec.Example is not null) text.Append($"\n## Example\n\n{spec.Example}\n");
        AppendConfig(text, capability.Config);
        AppendNamed(text, "Key types", capability.KeyTypes.Select(t => (t.Type, t.Summary)));
        AppendNamed(text, "Services", capability.Services.Select(s => (ServiceLabel(s), s.Summary)));
        AppendNamed(text, "Attributes", capability.Attributes.Select(a => ($"[{AttributeName(a.Type)}]", a.Summary)));
        return text.ToString();
    }

    public static string SearchResults(string query, IReadOnlyList<SearchHit> hits)
    {
        if (hits.Count == 0)
            return $"Nothing in the catalog matches \"{query}\". List every capability with altruist_capabilities.";
        var text = new StringBuilder($"Capabilities matching \"{query}\", best first. Read one with altruist_capability.\n\n");
        foreach (var hit in hits)
        {
            text.Append($"- `{hit.Capability.Spec.Name}`: {hit.Capability.Spec.Title}. {FirstSentence(hit.Capability.Spec.Summary)}\n");
            if (hit.Matches.Count > 0) text.Append($"  Matches: {string.Join(", ", hit.Matches.Select(m => $"`{m}`"))}\n");
        }
        return text.ToString();
    }

    private static void AppendList(StringBuilder text, string heading, IReadOnlyList<string> items)
    {
        if (items.Count == 0) return;
        text.Append($"\n**{heading}**\n\n");
        foreach (var item in items) text.Append($"- {item}\n");
    }

    private static void AppendConfig(StringBuilder text, IReadOnlyList<ConfigEntry> config)
    {
        if (config.Count == 0) return;
        text.Append("\n## Configuration\n\nKeys go in `config.yml` (kebab-case, nested under `altruist:`).\n\n");
        foreach (var entry in config)
        {
            var facts = new List<string>();
            if (entry.Type is not null) facts.Add(entry.Type);
            if (entry.Default is not null) facts.Add($"default `{entry.Default}`");
            if (entry.Live) facts.Add("follows reloads");
            if (entry.Gates.Count > 0) facts.Add($"read only when {string.Join(" and ", entry.Gates)}");
            var details = facts.Count == 0 ? "" : $" ({string.Join(", ", facts)})";
            text.Append($"- `{entry.Key}`{details}{Suffix(entry.Description)}\n");
        }
    }

    private static void AppendNamed(StringBuilder text, string heading, IEnumerable<(string Name, string? Summary)> items)
    {
        var list = items.ToList();
        if (list.Count == 0) return;
        text.Append($"\n## {heading}\n\n");
        foreach (var (name, summary) in list) text.Append($"- `{name}`{Suffix(summary)}\n");
    }

    private static string ServiceLabel(ServiceEntry service) => service.Kind switch
    {
        "portal" => $"{service.Type} (portal at {service.Contracts[0]})",
        "controller" => $"{service.Type} (HTTP at {service.Contracts[0]})",
        "configurationSection" => $"{service.Type} (binds {service.Contracts[0]})",
        _ when service.Contracts.Count > 0 && service.Contracts[0] != service.Type => $"{service.Type} as {service.Contracts[0]}",
        _ => service.Type,
    } + (service.Gates.Count > 0 ? $"; only when {string.Join(" and ", service.Gates)}" : "");

    private static string AttributeName(string type)
    {
        var name = type.Split('.').Last();
        return name.EndsWith("Attribute", StringComparison.Ordinal) ? name[..^"Attribute".Length] : name;
    }

    private static string DocLink(string path) => path.StartsWith('/') ? DocsSite + path : path;

    private static string Suffix(string? summary) => summary is null ? "" : $": {FirstSentence(summary)}";

    private static string FirstSentence(string text)
    {
        var end = text.IndexOf(". ", StringComparison.Ordinal);
        return end < 0 ? text : text[..(end + 1)];
    }
}
