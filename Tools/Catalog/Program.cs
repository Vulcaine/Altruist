using System.Reflection;
using System.Text.Json;
using System.Xml.Linq;
using Altruist.Catalog;

// dotnet run --project Tools/Catalog            writes catalog/capabilities.json and catalog/CAPABILITIES.md
// dotnet run --project Tools/Catalog -- --check  fails when they are stale or something belongs to no capability
var check = args.Contains("--check");
var root = FindRepoRoot(Directory.GetCurrentDirectory());
var projects = FrameworkProjects(root);

var assemblies = projects
    .Select(project => Path.Combine(AppContext.BaseDirectory, project.AssemblyName + ".dll"))
    .Where(File.Exists)
    .Select(Assembly.LoadFrom)
    .ToList();

var source = SourceIndex.FromDirectory(root, Path.Combine(root, "Altruist"));
var (surface, unlocated) = AssemblySurface.Create(source, XmlDocs.FromAssemblies(assemblies)).Read(assemblies);
var (specs, mapGaps) = CapabilityMap.FromFile(Path.Combine(root, "catalog", "map.yml"));
var missingPaths = specs
    .SelectMany(spec => spec.Paths.Where(path => !File.Exists(Path.Combine(root, path)) && !Directory.Exists(Path.Combine(root, path)))
        .Select(path => new CoverageGap(GapKind.MissingPath, path, $"listed by {spec.Name}")));

var result = CatalogAssembler.Assemble(new CatalogInputs(
    Version: File.ReadAllText(Path.Combine(root, "Altruist", "version.txt")).Trim(),
    Specs: specs,
    Surface: surface,
    PublishedPackages: PublishedPackages(root, projects),
    KnownTypes: assemblies.SelectMany(assembly => assembly.GetTypes()).Select(AssemblySurface.Name).ToHashSet(StringComparer.Ordinal)));

var gaps = mapGaps.Concat(unlocated).Concat(missingPaths).Concat(result.Gaps).ToList();
var outputs = new Dictionary<string, string>
{
    [Path.Combine(root, "catalog", "capabilities.json")] = JsonCatalogWriter.Write(result.Catalog),
    [Path.Combine(root, "catalog", "CAPABILITIES.md")] = MarkdownCatalogWriter.Write(result.Catalog),
};

foreach (var gap in gaps)
    Console.Error.WriteLine($"{gap.Kind}: {gap.Subject} ({gap.Detail})");

if (check)
{
    var stale = outputs
        .Where(output => !File.Exists(output.Key) || File.ReadAllText(output.Key).ReplaceLineEndings("\n") != output.Value)
        .Select(output => Path.GetRelativePath(root, output.Key))
        .ToList();
    foreach (var file in stale)
        Console.Error.WriteLine($"Stale: {file}. Run `dotnet run --project Tools/Catalog` and commit the result.");
    return gaps.Count > 0 ? 1 : stale.Count > 0 ? 2 : 0;
}

foreach (var (path, content) in outputs)
    File.WriteAllText(path, content);
var claimed = result.Catalog.Capabilities;
Console.WriteLine($"{claimed.Count} capabilities: {claimed.Sum(c => c.Services.Count)} services, " +
    $"{claimed.Sum(c => c.Config.Count)} config keys, {claimed.Sum(c => c.Attributes.Count)} attributes; {gaps.Count} gaps.");
return gaps.Count > 0 ? 1 : 0;

static string FindRepoRoot(string start)
{
    for (var directory = new DirectoryInfo(start); directory is not null; directory = directory.Parent)
        if (File.Exists(Path.Combine(directory.FullName, "Altruist", "version.txt")))
            return directory.FullName;
    throw new DirectoryNotFoundException($"No Altruist repository (Altruist/version.txt) above {start}.");
}

static IReadOnlyList<FrameworkProject> FrameworkProjects(string root) =>
    Directory.EnumerateFiles(Path.Combine(root, "Altruist"), "*.csproj", SearchOption.AllDirectories)
        .Concat(Directory.EnumerateFiles(Path.Combine(root, "CLI"), "*.csproj", SearchOption.AllDirectories))
        .Concat(Directory.EnumerateFiles(Path.Combine(root, "Templates"), "*.csproj", SearchOption.TopDirectoryOnly))
        .Concat(Directory.EnumerateDirectories(Path.Combine(root, "Templates"))
            .SelectMany(directory => Directory.EnumerateFiles(directory, "*.csproj", SearchOption.TopDirectoryOnly)))
        .Where(path => !path.Replace('\\', '/').Contains("/TestApp/"))
        .Select(FrameworkProject.From)
        .ToList();

static IReadOnlyList<string> PublishedPackages(string root, IReadOnlyList<FrameworkProject> projects)
{
    var nuget = projects.Where(project => project.Packable).Select(project => project.PackageId);
    var npm = Directory.EnumerateDirectories(Path.Combine(root, "Clients", "TypeScript"))
        .Select(directory => Path.Combine(directory, "package.json"))
        .Append(Path.Combine(root, "VSCode", "package.json"))
        .Where(File.Exists)
        .Select(path => JsonDocument.Parse(File.ReadAllText(path)).RootElement.GetProperty("name").GetString()!);
    return nuget.Concat(npm).Distinct().OrderBy(name => name, StringComparer.Ordinal).ToList();
}

internal sealed record FrameworkProject(string AssemblyName, string PackageId, bool Packable)
{
    public static FrameworkProject From(string csproj)
    {
        var properties = XDocument.Load(csproj).Descendants().Where(element => element.Parent?.Name.LocalName == "PropertyGroup").ToList();
        string? Property(string name) => properties.FirstOrDefault(element => element.Name.LocalName == name)?.Value.Trim();
        var assemblyName = Property("AssemblyName") ?? Path.GetFileNameWithoutExtension(csproj);
        return new FrameworkProject(assemblyName, Property("PackageId") ?? assemblyName,
            !string.Equals(Property("IsPackable"), "false", StringComparison.OrdinalIgnoreCase));
    }
}
