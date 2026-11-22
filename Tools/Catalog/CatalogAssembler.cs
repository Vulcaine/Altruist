namespace Altruist.Catalog;

/// <summary>What the assembler needs besides the map: the surface, the published packages and the known type names.</summary>
public sealed record CatalogInputs(
    string Version,
    IReadOnlyList<CapabilitySpec> Specs,
    Surface Surface,
    IReadOnlyList<string> PublishedPackages,
    IReadOnlySet<string> KnownTypes);

/// <summary>
/// Sorts the surface into capabilities. An item belongs to the capability that claims it most specifically: by type
/// name, then (for config keys) by the longest key prefix, then by the longest source path.
/// </summary>
public static class CatalogAssembler
{
    private const int TypeScore = 1_000_000;
    private const int ConfigScore = 100_000;

    public static AssemblyResult Assemble(CatalogInputs inputs)
    {
        var specs = inputs.Specs;
        var services = inputs.Surface.Services.Select(s => (Item: s, Owner: Owner(specs, s.Type, s.File, null))).ToList();
        var config = inputs.Surface.Config.Select(c => (Item: c, Owner: Owner(specs, c.ReadBy, c.File, c.Key))).ToList();
        var attributes = inputs.Surface.Attributes.Select(a => (Item: a, Owner: Owner(specs, a.Type, a.File, null))).ToList();
        var types = inputs.Surface.PublicTypes.Select(t => (Item: t, Owner: Owner(specs, t.Type, t.File, null))).ToList();
        var typeByName = inputs.Surface.PublicTypes.ToDictionary(t => t.Type, StringComparer.Ordinal);

        var capabilities = specs
            .Select(spec => new Capability(
                spec,
                spec.Types.Where(typeByName.ContainsKey).Select(type => typeByName[type]).ToList(),
                services.Where(s => s.Owner == spec.Name).Select(s => s.Item).ToList(),
                config.Where(c => c.Owner == spec.Name).Select(c => c.Item).ToList(),
                attributes.Where(a => a.Owner == spec.Name).Select(a => a.Item).ToList()))
            .ToList();

        var gaps = services.Where(s => s.Owner is null)
                .Select(s => new CoverageGap(GapKind.UnclaimedService, s.Item.Type, s.Item.File))
            .Concat(config.Where(c => c.Owner is null)
                .Select(c => new CoverageGap(GapKind.UnclaimedConfigKey, c.Item.Key, c.Item.File)))
            .Concat(attributes.Where(a => a.Owner is null)
                .Select(a => new CoverageGap(GapKind.UnclaimedAttribute, a.Item.Type, a.Item.File)))
            .Concat(types.Where(t => t.Owner is null)
                .Select(t => new CoverageGap(GapKind.UnclaimedType, t.Item.Type, t.Item.File)))
            .Concat(specs.SelectMany(spec => spec.Types.Where(type => !inputs.KnownTypes.Contains(type))
                .Select(type => new CoverageGap(GapKind.MissingType, type, $"listed by {spec.Name}"))))
            .Concat(inputs.PublishedPackages.Where(package => !specs.Any(spec => spec.Packages.Contains(package)))
                .Select(package => new CoverageGap(GapKind.UnlistedPackage, package, "no capability names it")))
            .ToList();

        return new AssemblyResult(new CapabilityCatalog(inputs.Version, capabilities), gaps);
    }

    private static string? Owner(IReadOnlyList<CapabilitySpec> specs, string? type, string file, string? key) =>
        specs
            .Select(spec => (spec.Name, Score: Score(spec, type, file, key)))
            .Where(candidate => candidate.Score > 0)
            .OrderByDescending(candidate => candidate.Score)
            .Select(candidate => candidate.Name)
            .FirstOrDefault();

    private static int Score(CapabilitySpec spec, string? type, string file, string? key)
    {
        if (type is not null && spec.Types.Contains(type)) return TypeScore;
        var configMatch = key is null ? 0 : spec.Config
            .Where(prefix => key.Equals(prefix, StringComparison.OrdinalIgnoreCase)
                || key.StartsWith(prefix + ":", StringComparison.OrdinalIgnoreCase))
            .Select(prefix => ConfigScore + prefix.Length)
            .DefaultIfEmpty(0)
            .Max();
        if (configMatch > 0) return configMatch;
        return spec.Paths
            .Where(path => file.StartsWith(path, StringComparison.Ordinal))
            .Select(path => path.Length)
            .DefaultIfEmpty(0)
            .Max();
    }
}
