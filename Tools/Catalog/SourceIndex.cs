using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Altruist.Catalog;

/// <summary>A config key written as a string literal, where it appears.</summary>
public sealed record LiteralKey(string Key, string File, int Line);

/// <summary>
/// What the framework's C# source says that reflection cannot: the file each type is declared in (capabilities are
/// placed by source path) and the config keys read by hand.
/// </summary>
public sealed class SourceIndex
{
    private const string KeyPrefix = "altruist:";

    private readonly ImmutableDictionary<string, string> _fileByType;

    private SourceIndex(ImmutableDictionary<string, string> fileByType, ImmutableArray<LiteralKey> literalKeys)
    {
        _fileByType = fileByType;
        LiteralKeys = literalKeys;
    }

    public ImmutableArray<LiteralKey> LiteralKeys { get; }

    /// <summary>Parses every .cs file under <paramref name="sourceRoot"/>; paths are kept relative to <paramref name="repoRoot"/>.</summary>
    public static SourceIndex FromDirectory(string repoRoot, string sourceRoot)
    {
        var files = Directory.EnumerateFiles(sourceRoot, "*.cs", SearchOption.AllDirectories)
            .Where(IsFrameworkSource)
            .Select(path => (Relative: ToRelative(repoRoot, path), Root: CSharpSyntaxTree.ParseText(File.ReadAllText(path)).GetRoot()))
            .OrderBy(file => file.Relative, StringComparer.Ordinal)
            .ToList();

        var fileByType = ImmutableDictionary.CreateBuilder<string, string>(StringComparer.Ordinal);
        foreach (var (relative, root) in files)
            foreach (var type in TopLevelTypes(root))
                fileByType.TryAdd(type, relative);

        var literalKeys = files.SelectMany(file => LiteralKeysIn(file.Relative, file.Root)).ToImmutableArray();
        return new SourceIndex(fileByType.ToImmutable(), literalKeys);
    }

    /// <summary>The file declaring <paramref name="type"/> (nested types map to their outermost declaration).</summary>
    public Optional<string> FileOf(Type type)
    {
        var outer = type;
        while (outer.DeclaringType is not null) outer = outer.DeclaringType;
        return _fileByType.TryGetValue(MetadataName(outer.Namespace, outer.Name), out var file)
            ? Optional<string>.Of(file)
            : Optional<string>.Empty;
    }

    private static bool IsFrameworkSource(string path)
    {
        var normalized = path.Replace('\\', '/');
        return !normalized.Contains("/bin/") && !normalized.Contains("/obj/") && !normalized.Contains("/TestApp/")
            && !normalized.Contains("/node_modules/");
    }

    private static string ToRelative(string repoRoot, string path) =>
        Path.GetRelativePath(repoRoot, path).Replace('\\', '/');

    /// <summary>The types declared directly in a namespace or the file, named as reflection names them (<c>Ns.Name`2</c>).</summary>
    private static IEnumerable<string> TopLevelTypes(SyntaxNode root) =>
        root.DescendantNodes(node => node is CompilationUnitSyntax or BaseNamespaceDeclarationSyntax)
            .OfType<MemberDeclarationSyntax>()
            .Where(member => member.Parent is CompilationUnitSyntax or BaseNamespaceDeclarationSyntax)
            .SelectMany(member => DeclaredName(member).Match(
                name => new[] { MetadataName(NamespaceOf(member), name) },
                () => Array.Empty<string>()));

    private static Optional<string> DeclaredName(MemberDeclarationSyntax member) => member switch
    {
        TypeDeclarationSyntax type => Optional<string>.Of(WithArity(type.Identifier.ValueText, type.Arity)),
        EnumDeclarationSyntax @enum => Optional<string>.Of(@enum.Identifier.ValueText),
        DelegateDeclarationSyntax @delegate => Optional<string>.Of(WithArity(@delegate.Identifier.ValueText, @delegate.Arity)),
        _ => Optional<string>.Empty,
    };

    private static string WithArity(string name, int arity) => arity == 0 ? name : $"{name}`{arity}";

    private static string NamespaceOf(SyntaxNode node) =>
        string.Join(".", node.Ancestors().OfType<BaseNamespaceDeclarationSyntax>().Reverse().Select(ns => ns.Name.ToString()));

    private static string MetadataName(string? ns, string name) => string.IsNullOrEmpty(ns) ? name : $"{ns}.{name}";

    // String literal tokens only: keys in comments and XML docs belong to imaginary example apps. A literal ending in
    // ':' is a prefix concatenated into something else (a cache key such as "altruist:ticket:" + hash), not a key.
    private static IEnumerable<LiteralKey> LiteralKeysIn(string file, SyntaxNode root) =>
        root.DescendantTokens()
            .Where(token => token.IsKind(SyntaxKind.StringLiteralToken))
            .Where(token => token.ValueText.StartsWith(KeyPrefix, StringComparison.Ordinal)
                && !token.ValueText.EndsWith(':')
                && token.ValueText.All(IsKeyCharacter))
            .Select(token => new LiteralKey(
                token.ValueText, file, token.GetLocation().GetLineSpan().StartLinePosition.Line + 1));

    private static bool IsKeyCharacter(char c) => char.IsAsciiLetterOrDigit(c) || c is ':' or '*' or '_' or '.' or '-';
}
