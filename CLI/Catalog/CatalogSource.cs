using System.Text.Json;

namespace Altruist.CLI.Catalog;

/// <summary>The capability catalog built into the CLI, so it always describes the framework version the CLI ships with.</summary>
public static class CatalogSource
{
    private const string ResourceName = "capabilities.json";

    private static readonly JsonSerializerOptions Options = new() { PropertyNameCaseInsensitive = true };

    public static CapabilityCatalog FromEmbeddedResource()
    {
        using var stream = typeof(CatalogSource).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException($"The CLI was built without its {ResourceName} resource.");
        return FromJson(stream);
    }

    public static CapabilityCatalog FromJson(Stream json) =>
        JsonSerializer.Deserialize<CapabilityCatalog>(json, Options)
        ?? throw new InvalidDataException("The capability catalog is empty.");
}
