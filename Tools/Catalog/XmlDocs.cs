using System.Collections.Immutable;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Altruist.Catalog;

/// <summary>The summaries and parameter docs the compiler wrote next to each framework assembly.</summary>
public sealed partial class XmlDocs
{
    private readonly ImmutableDictionary<string, string> _summaries;
    private readonly ImmutableDictionary<string, string> _constructorParameters;

    private XmlDocs(ImmutableDictionary<string, string> summaries, ImmutableDictionary<string, string> constructorParameters)
    {
        _summaries = summaries;
        _constructorParameters = constructorParameters;
    }

    /// <summary>Reads <c>Name.xml</c> beside each assembly; an assembly without one contributes nothing.</summary>
    public static XmlDocs FromAssemblies(IEnumerable<Assembly> assemblies)
    {
        var summaries = ImmutableDictionary.CreateBuilder<string, string>(StringComparer.Ordinal);
        var parameters = ImmutableDictionary.CreateBuilder<string, string>(StringComparer.Ordinal);
        foreach (var assembly in assemblies)
        {
            var path = Path.ChangeExtension(assembly.Location, ".xml");
            if (!File.Exists(path)) continue;
            foreach (var member in XDocument.Load(path).Descendants("member"))
                Collect(member, summaries, parameters);
        }
        return new XmlDocs(summaries.ToImmutable(), parameters.ToImmutable());
    }

    public Optional<string> OfType(Type type) => Lookup(_summaries, $"T:{DocName(type)}");

    public Optional<string> OfProperty(PropertyInfo property) =>
        Lookup(_summaries, $"P:{DocName(property.DeclaringType!)}.{property.Name}");

    /// <summary>The docs of a constructor parameter; constructors of one type are not told apart by signature.</summary>
    public Optional<string> OfConstructorParameter(Type type, string parameter) =>
        Lookup(_constructorParameters, $"{DocName(type)}#{parameter}");

    private static void Collect(
        XElement member,
        ImmutableDictionary<string, string>.Builder summaries,
        ImmutableDictionary<string, string>.Builder parameters)
    {
        var name = (string?)member.Attribute("name");
        if (name is null) return;
        var summary = member.Element("summary");
        if (summary is not null) summaries.TryAdd(name, Flatten(summary));
        if (!name.StartsWith("M:", StringComparison.Ordinal) || !name.Contains(".#ctor", StringComparison.Ordinal)) return;
        var type = name[2..name.IndexOf(".#ctor", StringComparison.Ordinal)];
        foreach (var param in member.Elements("param"))
            parameters.TryAdd($"{type}#{(string?)param.Attribute("name")}", Flatten(param));
    }

    private static Optional<string> Lookup(ImmutableDictionary<string, string> docs, string id) =>
        docs.TryGetValue(id, out var text) && text.Length > 0 ? Optional<string>.Of(text) : Optional<string>.Empty;

    private static string DocName(Type type) => (type.FullName ?? type.Name).Replace('+', '.');

    /// <summary>The element's text with references written as the short name they point at.</summary>
    private static string Flatten(XElement element)
    {
        var parts = element.Nodes().Select(node => node switch
        {
            XText text => text.Value,
            XElement { Name.LocalName: "see" or "seealso" } see => ReferenceText(see),
            XElement { Name.LocalName: "paramref" or "typeparamref" } reference => (string?)reference.Attribute("name") ?? "",
            XElement { Name.LocalName: "c" } code => $"`{code.Value}`",
            XElement { Name.LocalName: "para" } para => $" {Flatten(para)} ",
            XElement other => Flatten(other),
            _ => "",
        });
        return Whitespace().Replace(string.Concat(parts), " ").Trim();
    }

    private static string ReferenceText(XElement see)
    {
        if (see.Value.Length > 0) return see.Value;
        var target = (string?)see.Attribute("cref") ?? (string?)see.Attribute("langword") ?? (string?)see.Attribute("href") ?? "";
        var withoutPrefix = target.Length > 2 && target[1] == ':' ? target[2..] : target;
        var withoutSignature = withoutPrefix.Split('(')[0];
        var shortName = withoutSignature.Split('.').Last().Split('`')[0];
        return $"`{shortName}`";
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();
}
