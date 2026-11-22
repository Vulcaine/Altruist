namespace Altruist.Catalog;

/// <summary>A system an agent or developer reaches for, as written in catalog/map.yml.</summary>
public sealed record CapabilitySpec(
    string Name,
    string Title,
    string Summary,
    IReadOnlyList<string> Use,
    IReadOnlyList<string> Avoid,
    IReadOnlyList<string> Packages,
    IReadOnlyList<string> Docs,
    IReadOnlyList<string> Paths,
    IReadOnlyList<string> Types,
    IReadOnlyList<string> Config,
    string? Example);

public enum ConfigOrigin
{
    /// <summary>An <c>[AppConfigValue]</c> constructor parameter or property.</summary>
    Value,
    /// <summary>A property of a <c>[ConfigurationProperties]</c> class.</summary>
    Section,
    /// <summary>A gate of <c>[ConditionalOnConfig]</c>.</summary>
    Gate,
    /// <summary>A key read by hand: an <c>"altruist:..."</c> string in the source.</summary>
    Literal,
}

public sealed record ConfigEntry(
    string Key,
    string? Type,
    string? Default,
    string? Description,
    ConfigOrigin Origin,
    bool Live,
    string? ReadBy,
    string File,
    IReadOnlyList<string> Gates);

public enum ServiceKind { Service, ConfigurationStep, ConfigurationSection, Portal, Controller }

public sealed record ServiceEntry(
    string Type,
    ServiceKind Kind,
    IReadOnlyList<string> Contracts,
    string? Summary,
    string File,
    IReadOnlyList<string> Gates);

public sealed record AttributeEntry(string Type, string? Summary, string File);

/// <summary>A public type: listed when a capability names it as a key type, otherwise only checked for coverage.</summary>
public sealed record TypeEntry(string Type, string? Summary, string File);

/// <summary>Everything the framework exposes, before it is sorted into capabilities.</summary>
public sealed record Surface(
    IReadOnlyList<ServiceEntry> Services,
    IReadOnlyList<ConfigEntry> Config,
    IReadOnlyList<AttributeEntry> Attributes,
    IReadOnlyList<TypeEntry> PublicTypes);

public sealed record Capability(
    CapabilitySpec Spec,
    IReadOnlyList<TypeEntry> KeyTypes,
    IReadOnlyList<ServiceEntry> Services,
    IReadOnlyList<ConfigEntry> Config,
    IReadOnlyList<AttributeEntry> Attributes);

public sealed record CapabilityCatalog(string Version, IReadOnlyList<Capability> Capabilities);

public enum GapKind
{
    /// <summary>A map entry without a name, title or summary, or with a name used twice.</summary>
    InvalidCapability,
    /// <summary>A service, configuration step, section or portal no capability claims.</summary>
    UnclaimedService,
    UnclaimedConfigKey,
    UnclaimedAttribute,
    /// <summary>A public type in a file no capability path covers.</summary>
    UnclaimedType,
    /// <summary>A capability path that is neither a file nor a directory.</summary>
    MissingPath,
    /// <summary>A capability type that no framework assembly defines.</summary>
    MissingType,
    /// <summary>A published package that no capability names.</summary>
    UnlistedPackage,
    /// <summary>A public type whose source file was not found, so it cannot be placed by path.</summary>
    UnlocatedType,
}

public sealed record CoverageGap(GapKind Kind, string Subject, string Detail);

public sealed record AssemblyResult(CapabilityCatalog Catalog, IReadOnlyList<CoverageGap> Gaps);

/// <summary>A value that may be absent.</summary>
public readonly record struct Optional<T>
{
    private readonly T? _value;

    private Optional(T value)
    {
        _value = value;
        HasValue = true;
    }

    public bool HasValue { get; }

    public static Optional<T> Empty => default;

    public static Optional<T> Of(T value) => new(value);

    public TResult Match<TResult>(Func<T, TResult> some, Func<TResult> none) => HasValue ? some(_value!) : none();

    public T OrElse(T fallback) => HasValue ? _value! : fallback;

    /// <summary>The value, or the type's default when absent: for filling nullable fields of a record.</summary>
    public T? OrDefault() => _value;
}
