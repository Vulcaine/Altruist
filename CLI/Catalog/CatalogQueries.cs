using System.Collections.Immutable;

namespace Altruist.CLI.Catalog;

public abstract record CapabilityLookup
{
    private CapabilityLookup() { }

    public sealed record Found(Capability Capability) : CapabilityLookup;

    /// <summary>No capability has that name; <see cref="Suggestions"/> are the names that share a word with it.</summary>
    public sealed record NotFound(string Name, IReadOnlyList<string> Suggestions) : CapabilityLookup;
}

/// <summary>A capability matching a search, with the config keys, types and attributes that matched.</summary>
public sealed record SearchHit(Capability Capability, int Score, IReadOnlyList<string> Matches);

public static class CatalogQueries
{
    private const int NameWeight = 10;
    private const int MemberWeight = 5;
    private const int TextWeight = 2;
    private const int MaxMatchesShown = 6;
    // A capability with several keys or types named after a term is about it; past three it is just a big capability.
    private const int MaxMembersCounted = 3;

    public static CapabilityLookup Find(CapabilityCatalog catalog, string name)
    {
        var found = catalog.Capabilities.FirstOrDefault(c => string.Equals(c.Spec.Name, name.Trim(), StringComparison.OrdinalIgnoreCase));
        if (found is not null) return new CapabilityLookup.Found(found);
        var words = Terms(name.Replace('-', ' '));
        var suggestions = catalog.Capabilities
            .Where(c => words.Any(word => c.Spec.Name.Contains(word, StringComparison.OrdinalIgnoreCase)
                || c.Spec.Title.Contains(word, StringComparison.OrdinalIgnoreCase)))
            .Select(c => c.Spec.Name)
            .ToImmutableArray();
        return new CapabilityLookup.NotFound(name, suggestions);
    }

    /// <summary>
    /// Capabilities ranked by how well they match <paramref name="query"/>: words in the name or title count most, then
    /// config keys, types, services and attributes (which are also returned), then the summary and use cases.
    /// </summary>
    public static IReadOnlyList<SearchHit> Search(CapabilityCatalog catalog, string query, int limit)
    {
        var terms = Terms(query);
        return catalog.Capabilities
            .Select(capability => Score(capability, terms))
            .Where(hit => hit.Score > 0)
            .OrderByDescending(hit => hit.Score)
            .ThenBy(hit => hit.Capability.Spec.Name, StringComparer.Ordinal)
            .Take(limit)
            .ToImmutableArray();
    }

    private static SearchHit Score(Capability capability, IReadOnlyList<string> terms)
    {
        var spec = capability.Spec;
        var members = Members(capability);
        var text = string.Join(" ", spec.Summary, string.Join(" ", spec.Use), string.Join(" ", spec.Packages));
        var score = 0;
        foreach (var term in terms)
        {
            if (Contains(spec.Name, term) || Contains(spec.Title, term)) score += NameWeight;
            score += MemberWeight * Math.Min(MaxMembersCounted, members.Count(member => Contains(member, term)));
            if (Contains(text, term)) score += TextWeight;
        }
        var matches = members.Where(member => terms.Any(term => Contains(member, term))).Take(MaxMatchesShown).ToImmutableArray();
        return new SearchHit(capability, score, matches);
    }

    private static IReadOnlyList<string> Members(Capability capability) =>
        capability.Config.Select(entry => entry.Key)
            .Concat(capability.KeyTypes.Select(type => type.Type))
            .Concat(capability.Services.Select(service => service.Type))
            .Concat(capability.Attributes.Select(attribute => attribute.Type))
            .Distinct(StringComparer.Ordinal)
            .ToImmutableArray();

    private static bool Contains(string text, string term) => text.Contains(term, StringComparison.OrdinalIgnoreCase);

    // Single letters match almost everything and carry no meaning.
    private static IReadOnlyList<string> Terms(string query) =>
        query.Split([' ', ',', ';', '\t', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(term => term.Length > 1)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToImmutableArray();
}
