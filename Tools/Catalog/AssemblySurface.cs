using System.Reflection;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Configuration;

namespace Altruist.Catalog;

/// <summary>Reads the services, config keys and attributes the framework assemblies declare.</summary>
public sealed class AssemblySurface
{
    private const BindingFlags Members = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

    private readonly SourceIndex _source;
    private readonly XmlDocs _docs;

    private AssemblySurface(SourceIndex source, XmlDocs docs)
    {
        _source = source;
        _docs = docs;
    }

    public static AssemblySurface Create(SourceIndex source, XmlDocs docs) => new(source, docs);

    /// <summary>The surface of <paramref name="assemblies"/>, and the public types whose source file is unknown.</summary>
    public (Surface Surface, IReadOnlyList<CoverageGap> Gaps) Read(IReadOnlyList<Assembly> assemblies)
    {
        var types = assemblies
            .SelectMany(assembly => assembly.GetTypes())
            .Where(type => !type.IsDefined(typeof(CompilerGeneratedAttribute), false))
            .OrderBy(type => type.FullName, StringComparer.Ordinal)
            .ToList();

        var located = types
            .Select(type => (Type: type, File: _source.FileOf(type)))
            .ToList();

        var gaps = located
            .Where(entry => !entry.File.HasValue && IsSurfaceType(entry.Type))
            .Select(entry => new CoverageGap(GapKind.UnlocatedType, entry.Type.FullName ?? entry.Type.Name, "no .cs file declares it"))
            .ToList();

        var placed = located.Where(entry => entry.File.HasValue).Select(entry => (entry.Type, File: entry.File.OrElse(""))).ToList();
        var services = placed.SelectMany(entry => ServicesOf(entry.Type, entry.File)).ToList();
        var config = placed.SelectMany(entry => ConfigOf(entry.Type, entry.File)).ToList();
        var attributes = placed
            .Where(entry => entry.Type.IsPublic && typeof(Attribute).IsAssignableFrom(entry.Type))
            .Select(entry => new AttributeEntry(entry.Type.FullName!, _docs.OfType(entry.Type).OrDefault(), entry.File))
            .ToList();

        var publicTypes = placed
            .Where(entry => entry.Type.IsPublic && !typeof(Attribute).IsAssignableFrom(entry.Type))
            .Select(entry => new TypeEntry(Name(entry.Type), _docs.OfType(entry.Type).OrDefault(), entry.File))
            .ToList();

        return (new Surface(services, MergeLiterals(config), attributes, publicTypes), gaps);
    }

    /// <summary>The route of an MVC controller, read by name: the catalog does not reference ASP.NET Core.</summary>
    private static string? RouteOf(Type type)
    {
        var attributes = type.GetCustomAttributes(false);
        if (!attributes.Any(attribute => attribute.GetType().Name == "ApiControllerAttribute")) return null;
        var route = attributes.FirstOrDefault(attribute => attribute.GetType().Name == "RouteAttribute");
        return route?.GetType().GetProperty("Template")?.GetValue(route) as string ?? "";
    }

    private static bool IsSurfaceType(Type type) =>
        type.IsDefined(typeof(ServiceAttribute), false)
        || type.IsDefined(typeof(ServiceConfigurationAttribute), false)
        || type.IsDefined(typeof(ConfigurationPropertiesAttribute), false)
        || type.IsPublic;

    private IEnumerable<ServiceEntry> ServicesOf(Type type, string file)
    {
        var gates = GatesOf(type);
        var summary = _docs.OfType(type).OrDefault();
        foreach (var service in type.GetCustomAttributes<ServiceAttribute>(false))
            yield return new ServiceEntry(Name(type), ServiceKind.Service, [Name(service.ServiceType ?? type)], summary, file, gates);
        if (type.GetCustomAttribute<ServiceConfigurationAttribute>(false) is { } step)
            yield return new ServiceEntry(Name(type), ServiceKind.ConfigurationStep, [Name(step.ServiceType ?? type)], summary, file, gates);
        if (type.GetCustomAttribute<ConfigurationPropertiesAttribute>(false) is { } section)
            yield return new ServiceEntry(Name(type), ServiceKind.ConfigurationSection, [section.Path], summary, file, gates);
        if (type.GetCustomAttribute<PortalAttribute>(false) is { } portal)
            yield return new ServiceEntry(Name(type), ServiceKind.Portal, [portal.Endpoint], summary, file, gates);
        if (RouteOf(type) is { } route)
            yield return new ServiceEntry(Name(type), ServiceKind.Controller, [route], summary, file, gates);
    }

    private IEnumerable<ConfigEntry> ConfigOf(Type type, string file)
    {
        var gates = GatesOf(type);
        var listPath = type.GetCustomAttributes<ConditionalOnConfigAttribute>(false)
            .FirstOrDefault(gate => gate.KeyField is not null)?.Path;

        var values = type.GetConstructors(Members)
            .SelectMany(constructor => constructor.GetParameters())
            .Select(parameter => (Attribute: parameter.GetCustomAttribute<AppConfigValueAttribute>(), Target: parameter.ParameterType,
                Description: _docs.OfConstructorParameter(type, parameter.Name ?? "")))
            .Concat(type.GetProperties(Members | BindingFlags.DeclaredOnly)
                .Select(property => (Attribute: property.GetCustomAttribute<AppConfigValueAttribute>(), Target: property.PropertyType,
                    Description: _docs.OfProperty(property))))
            .Where(value => value.Attribute is not null)
            .Select(value => ValueEntry(value.Attribute!, value.Target, value.Description, listPath, type, file, gates));

        var gateKeys = type.GetCustomAttributes<ConditionalOnConfigAttribute>(false)
            .Select(gate => new ConfigEntry(gate.Path, gate.KeyField is null ? null : "list", null,
                gate.HavingValue is null ? null : $"Set to `{gate.HavingValue}` to turn {Name(type)} on.",
                ConfigOrigin.Gate, false, Name(type), file, gates));

        var sections = type.GetCustomAttribute<ConfigurationPropertiesAttribute>(false) is { } section
            ? SectionEntries(type, section.Path, file, gates)
            : [];

        return values.Concat(gateKeys).Concat(sections);
    }

    private static ConfigEntry ValueEntry(
        AppConfigValueAttribute attribute, Type target, Optional<string> description, string? listPath,
        Type readBy, string file, IReadOnlyList<string> gates)
    {
        var live = target.IsGenericType && target.GetGenericTypeDefinition() == typeof(ILiveConfigValue<>);
        var valueType = live ? target.GetGenericArguments()[0] : target;
        var key = attribute.Path.StartsWith('*') && listPath is not null ? $"{listPath}:*{attribute.Path[1..]}" : attribute.Path;
        return new ConfigEntry(key, Name(valueType), attribute.Default, description.OrDefault(), ConfigOrigin.Value, live,
            Name(readBy), file, gates);
    }

    private IEnumerable<ConfigEntry> SectionEntries(Type type, string path, string file, IReadOnlyList<string> gates)
    {
        var instance = type.GetConstructor(Type.EmptyTypes) is null ? null : Activator.CreateInstance(type);
        return Walk(type, instance, path, [type], type, file, gates);
    }

    private IEnumerable<ConfigEntry> Walk(
        Type type, object? instance, string path, IReadOnlyList<Type> visiting, Type owner, string file, IReadOnlyList<string> gates)
    {
        foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance).Where(p => p.GetIndexParameters().Length == 0))
        {
            // The binder matches property names ignoring case, and does not read kebab-case: camelCase is what binds.
            var key = $"{path}:{property.GetCustomAttribute<ConfigurationKeyNameAttribute>()?.Name ?? CamelCase(property.Name)}";
            var value = instance is null ? null : property.GetValue(instance);
            var description = _docs.OfProperty(property).OrDefault();
            var shape = Shape(property.PropertyType);
            if (shape.Leaf)
            {
                if (property.CanWrite)
                    yield return new ConfigEntry(key, Name(property.PropertyType), Format(value), description, ConfigOrigin.Section,
                        false, Name(owner), file, gates);
                continue;
            }
            var itemKey = shape.Element is null ? key : $"{key}:*";
            var itemType = shape.Element ?? property.PropertyType;
            if (Shape(itemType).Leaf)
            {
                yield return new ConfigEntry(itemKey, Name(itemType), shape.Element is null ? Format(value) : null, description,
                    ConfigOrigin.Section, false, Name(owner), file, gates);
                continue;
            }
            if (visiting.Contains(itemType) || visiting.Count > 6) continue;
            foreach (var child in Walk(itemType, shape.Element is null ? value : null, itemKey, [.. visiting, itemType], owner, file, gates))
                yield return child;
        }
    }

    /// <summary>Whether a type binds as one value, and the element type when it binds as a list or a map.</summary>
    private static (bool Leaf, Type? Element) Shape(Type type)
    {
        var underlying = Nullable.GetUnderlyingType(type) ?? type;
        if (underlying.IsPrimitive || underlying.IsEnum || underlying == typeof(string) || underlying == typeof(decimal)
            || underlying == typeof(TimeSpan) || underlying == typeof(DateTime) || underlying == typeof(DateTimeOffset)
            || underlying == typeof(Guid) || underlying == typeof(Uri))
            return (true, null);
        if (underlying.IsArray) return (false, underlying.GetElementType());
        var dictionary = underlying.GetInterfaces().Append(underlying)
            .FirstOrDefault(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IDictionary<,>));
        if (dictionary is not null) return (false, dictionary.GetGenericArguments()[1]);
        var enumerable = underlying.GetInterfaces().Append(underlying)
            .FirstOrDefault(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IEnumerable<>));
        return enumerable is not null ? (false, enumerable.GetGenericArguments()[0]) : (false, null);
    }

    private static string? Format(object? value) => value switch
    {
        null => null,
        bool flag => flag ? "true" : "false",
        string text => text.Length == 0 ? null : text,
        IFormattable formattable => formattable.ToString(null, System.Globalization.CultureInfo.InvariantCulture),
        _ => value.ToString(),
    };

    private static IReadOnlyList<string> GatesOf(Type type) =>
        type.GetCustomAttributes<ConditionalOnConfigAttribute>(false)
            .Select(gate => gate.KeyField is not null ? $"one per item of {gate.Path} (keyed by {gate.KeyField})"
                : gate.HavingValue is null ? $"{gate.Path} is set" : $"{gate.Path} = {gate.HavingValue}")
            .Concat(type.GetCustomAttributes<ConditionalOnAssemblyAttribute>(false).Select(gate => $"assembly {gate.AssemblyName} is referenced"))
            .Concat(type.GetCustomAttributes<ConditionalOnMissingServiceAttribute>(false).Select(_ => "no other implementation is registered"))
            .ToList();

    /// <summary>
    /// Adds the keys read by hand. A literal that is a known key, or the section of known keys, adds nothing; a
    /// list index (<c>:0</c>) is not a key.
    /// </summary>
    private IReadOnlyList<ConfigEntry> MergeLiterals(IReadOnlyList<ConfigEntry> declared)
    {
        var known = declared.Select(entry => entry.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var literals = _source.LiteralKeys
            .Where(literal => !literal.Key.Split(':')[^1].All(char.IsDigit))
            .Where(literal => !known.Contains(literal.Key))
            .GroupBy(literal => literal.Key, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToList();
        var allKeys = known.Concat(literals.Select(literal => literal.Key)).ToList();
        var leafLiterals = literals
            .Where(literal => !allKeys.Any(key => key.StartsWith(literal.Key + ":", StringComparison.OrdinalIgnoreCase)))
            .Select(literal => new ConfigEntry(literal.Key, null, null, null, ConfigOrigin.Literal, false, null,
                $"{literal.File}:{literal.Line}", []));

        return declared
            .GroupBy(entry => entry.Key, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.OrderBy(entry => entry.Origin).ThenByDescending(entry => entry.Description?.Length ?? 0).First())
            .Concat(leafLiterals)
            .OrderBy(entry => entry.Key, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static string CamelCase(string name) => name.Length == 0 ? name : char.ToLowerInvariant(name[0]) + name[1..];

    /// <summary>A C# spelling of the type: <c>int</c>, <c>List&lt;string&gt;</c>, <c>Altruist.Gaming.Room</c>.</summary>
    public static string Name(Type type)
    {
        if (Nullable.GetUnderlyingType(type) is { } inner) return $"{Name(inner)}?";
        if (Aliases.TryGetValue(type, out var alias)) return alias;
        if (type.IsArray) return $"{Name(type.GetElementType()!)}[]";
        if (!type.IsGenericType) return (type.FullName ?? type.Name).Replace('+', '.');
        var definition = (type.GetGenericTypeDefinition().FullName ?? type.Name).Split('`')[0].Replace('+', '.');
        if (definition.StartsWith("System.", StringComparison.Ordinal)) definition = definition.Split('.').Last();
        return $"{definition}<{string.Join(", ", type.GetGenericArguments().Select(Name))}>";
    }

    private static readonly IReadOnlyDictionary<Type, string> Aliases = new Dictionary<Type, string>
    {
        [typeof(bool)] = "bool", [typeof(byte)] = "byte", [typeof(short)] = "short", [typeof(int)] = "int",
        [typeof(long)] = "long", [typeof(float)] = "float", [typeof(double)] = "double", [typeof(decimal)] = "decimal",
        [typeof(string)] = "string", [typeof(uint)] = "uint", [typeof(ulong)] = "ulong", [typeof(ushort)] = "ushort",
        [typeof(TimeSpan)] = "TimeSpan", [typeof(object)] = "object",
    };
}
