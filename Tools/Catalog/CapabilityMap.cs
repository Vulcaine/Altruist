using Microsoft.Extensions.Configuration;

namespace Altruist.Catalog;

/// <summary>Reads catalog/map.yml, the hand-written part of the catalog.</summary>
public static class CapabilityMap
{
    public static (IReadOnlyList<CapabilitySpec> Specs, IReadOnlyList<CoverageGap> Gaps) FromFile(string path)
    {
        var configuration = new ConfigurationBuilder().AddYamlFile(Path.GetFullPath(path), optional: false).Build();
        var specs = configuration.GetSection("capabilities").GetChildren()
            .OrderBy(child => int.Parse(child.Key, System.Globalization.CultureInfo.InvariantCulture))
            .Select(ToSpec)
            .ToList();
        return (specs, Validate(specs));
    }

    private static CapabilitySpec ToSpec(IConfigurationSection section) => new(
        Name: section["name"] ?? "",
        Title: section["title"] ?? "",
        Summary: section["summary"]?.Trim() ?? "",
        Use: List(section, "use"),
        Avoid: List(section, "avoid"),
        Packages: List(section, "packages"),
        Docs: List(section, "docs"),
        Paths: List(section, "paths"),
        Types: List(section, "types"),
        Config: List(section, "config"),
        Example: section["example"]?.TrimEnd());

    private static IReadOnlyList<string> List(IConfigurationSection section, string key) =>
        section.GetSection(key).GetChildren()
            .OrderBy(child => int.Parse(child.Key, System.Globalization.CultureInfo.InvariantCulture))
            .Select(child => child.Value?.Trim() ?? "")
            .Where(value => value.Length > 0)
            .ToList();

    private static IReadOnlyList<CoverageGap> Validate(IReadOnlyList<CapabilitySpec> specs)
    {
        var incomplete = specs
            .Where(spec => spec.Name.Length == 0 || spec.Title.Length == 0 || spec.Summary.Length == 0)
            .Select(spec => new CoverageGap(GapKind.InvalidCapability, spec.Name.Length == 0 ? spec.Title : spec.Name,
                "needs a name, a title and a summary"));
        var duplicates = specs
            .GroupBy(spec => spec.Name, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1)
            .Select(group => new CoverageGap(GapKind.InvalidCapability, group.Key, $"named {group.Count()} times"));
        return incomplete.Concat(duplicates).ToList();
    }
}
