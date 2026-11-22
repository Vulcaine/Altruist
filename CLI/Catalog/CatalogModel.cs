namespace Altruist.CLI.Catalog;

// The shape of catalog/capabilities.json, which Tools/Catalog writes. Only what the CLI shows is read.

public sealed record CapabilityCatalog(string Version, IReadOnlyList<Capability> Capabilities);

public sealed record Capability(
    CapabilitySpec Spec,
    IReadOnlyList<TypeEntry> KeyTypes,
    IReadOnlyList<ServiceEntry> Services,
    IReadOnlyList<ConfigEntry> Config,
    IReadOnlyList<AttributeEntry> Attributes);

public sealed record CapabilitySpec(
    string Name,
    string Title,
    string Summary,
    IReadOnlyList<string> Use,
    IReadOnlyList<string> Avoid,
    IReadOnlyList<string> Packages,
    IReadOnlyList<string> Docs,
    string? Example);

public sealed record TypeEntry(string Type, string? Summary);

public sealed record ServiceEntry(string Type, string Kind, IReadOnlyList<string> Contracts, string? Summary, IReadOnlyList<string> Gates);

public sealed record ConfigEntry(
    string Key,
    string? Type,
    string? Default,
    string? Description,
    bool Live,
    IReadOnlyList<string> Gates);

public sealed record AttributeEntry(string Type, string? Summary);
