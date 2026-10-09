using System.Text.Json;

namespace Altruist;

// ---------- List<string> ----------
/// <summary>
/// Converts a config string to <c>List&lt;string&gt;</c>: a JSON array (<c>["a","b"]</c>) or a comma-separated
/// list (<c>a, b</c>, entries trimmed, empty entries dropped). Blank input yields an empty list.
/// </summary>
/// <remarks>
/// Built-in <see cref="IConfigConverter"/> (registered as a singleton via <see cref="ConfigConverterAttribute"/>).
/// The DI config pipeline uses converters to turn string values into the target type, chiefly the
/// <c>Default</c> string of <c>[AppConfigValue]</c> for non-simple types; a config section that exists is bound
/// with the configuration binder instead. You never call it directly; to support another type, write your own
/// <c>[ConfigConverter(typeof(T))]</c> class implementing <see cref="IConfigConverter{T}"/>.
/// </remarks>
/// <example>
/// <code>
/// public MyService([AppConfigValue("myapp:regions", "eu,us")] List&lt;string&gt; regions) { }
/// </code>
/// </example>
[ConfigConverter(typeof(List<string>))]
public sealed class ListStringConfigConverter : IConfigConverter<List<string>>
{
    /// <inheritdoc/>
    public Type TargetType => typeof(List<string>);

    private readonly JsonSerializerOptions _jsonOptions;

    /// <summary>Created by DI with the framework's shared JSON options.</summary>
    /// <param name="options">JSON options used for the JSON-array form.</param>
    public ListStringConfigConverter(JsonSerializerOptions options)
    {
        _jsonOptions = options;
    }

    /// <summary>Parses <paramref name="value"/> as a JSON array or comma-separated list; never returns null.</summary>
    /// <param name="value">Raw config string.</param>
    /// <exception cref="JsonException">Input starts with <c>[</c> but is not a valid JSON string array.</exception>
    public List<string>? Convert(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return new List<string>();

        var s = value.Trim();

        // JSON array?
        if (s.StartsWith("["))
            return JsonSerializer.Deserialize<List<string>>(s, _jsonOptions)
                   ?? new List<string>();

        // CSV
        return s.Split(',', StringSplitOptions.RemoveEmptyEntries)
                .Select(x => x.Trim())
                .ToList();
    }

    object? IConfigConverter.Convert(string value) => Convert(value);
}

// ---------- IEnumerable<string> ----------
/// <summary>
/// Converts a config string to <c>IEnumerable&lt;string&gt;</c> using the same rules as
/// <see cref="ListStringConfigConverter"/> (JSON array or comma-separated).
/// </summary>
/// <remarks>
/// Built-in <see cref="IConfigConverter"/> (registered as a singleton via <see cref="ConfigConverterAttribute"/>).
/// The DI config pipeline uses converters to turn string values into the target type, chiefly the
/// <c>Default</c> string of <c>[AppConfigValue]</c> for non-simple types; a config section that exists is bound
/// with the configuration binder instead. You never call it directly; to support another type, write your own
/// <c>[ConfigConverter(typeof(T))]</c> class implementing <see cref="IConfigConverter{T}"/>.
/// </remarks>
[ConfigConverter(typeof(IEnumerable<string>))]
public sealed class EnumerableStringConfigConverter : IConfigConverter<IEnumerable<string>>
{
    /// <inheritdoc/>
    public Type TargetType => typeof(IEnumerable<string>);

    private readonly ListStringConfigConverter _converter;

    /// <summary>Created by DI.</summary>
    /// <param name="converter">The list converter that does the parsing.</param>
    public EnumerableStringConfigConverter(ListStringConfigConverter converter)
    {
        _converter = converter;
    }

    /// <summary>Parses <paramref name="value"/> like <see cref="ListStringConfigConverter.Convert(string)"/>; never returns null.</summary>
    /// <param name="value">Raw config string.</param>
    public IEnumerable<string>? Convert(string value)
    {
        var list = _converter.Convert(value);
        return list ?? Enumerable.Empty<string>();
    }

    object? IConfigConverter.Convert(string value) => Convert(value);
}

// ---------- IReadOnlyList<string> ----------
/// <summary>
/// Converts a config string to <c>IReadOnlyList&lt;string&gt;</c> using the same rules as
/// <see cref="ListStringConfigConverter"/> (JSON array or comma-separated).
/// </summary>
/// <remarks>
/// Built-in <see cref="IConfigConverter"/> (registered as a singleton via <see cref="ConfigConverterAttribute"/>).
/// The DI config pipeline uses converters to turn string values into the target type, chiefly the
/// <c>Default</c> string of <c>[AppConfigValue]</c> for non-simple types; a config section that exists is bound
/// with the configuration binder instead. You never call it directly; to support another type, write your own
/// <c>[ConfigConverter(typeof(T))]</c> class implementing <see cref="IConfigConverter{T}"/>.
/// </remarks>
[ConfigConverter(typeof(IReadOnlyList<string>))]
public sealed class ReadOnlyListStringConfigConverter : IConfigConverter<IReadOnlyList<string>>
{
    /// <inheritdoc/>
    public Type TargetType => typeof(IReadOnlyList<string>);

    private readonly ListStringConfigConverter _converter;

    /// <summary>Created by DI.</summary>
    /// <param name="converter">The list converter that does the parsing.</param>
    public ReadOnlyListStringConfigConverter(ListStringConfigConverter converter)
    {
        _converter = converter;
    }

    /// <summary>Parses <paramref name="value"/> like <see cref="ListStringConfigConverter.Convert(string)"/>; never returns null.</summary>
    /// <param name="value">Raw config string.</param>
    public IReadOnlyList<string>? Convert(string value)
    {
        var list = _converter.Convert(value);
        return list ?? [];
    }

    object? IConfigConverter.Convert(string value) => Convert(value);
}

// ---------- Dictionary<string,string> ----------
/// <summary>
/// Converts a config string to <c>Dictionary&lt;string, string&gt;</c>: a JSON object (<c>{"k":"v"}</c>) or
/// <c>key=value</c> pairs separated by <c>,</c> or <c>;</c> (keys/values trimmed; pairs without a key or value are
/// dropped; later duplicates win). Blank input yields an empty dictionary.
/// </summary>
/// <remarks>
/// The <c>key=value</c> and blank forms use a case-insensitive key comparer; the JSON form uses the default
/// (case-sensitive) comparer. Values cannot contain <c>,</c> or <c>;</c> in the pair form; use JSON for those.
/// </remarks>
[ConfigConverter(typeof(Dictionary<string, string>))]
public sealed class DictionaryStringStringConfigConverter : IConfigConverter<Dictionary<string, string>>
{
    /// <inheritdoc/>
    public Type TargetType => typeof(Dictionary<string, string>);

    private readonly JsonSerializerOptions _jsonOptions;

    /// <summary>Created by DI with the framework's shared JSON options.</summary>
    /// <param name="options">JSON options used for the JSON-object form.</param>
    public DictionaryStringStringConfigConverter(JsonSerializerOptions options) => _jsonOptions = options;

    /// <summary>Parses <paramref name="value"/> as a JSON object or <c>k=v</c> pairs; never returns null.</summary>
    /// <param name="value">Raw config string.</param>
    /// <exception cref="JsonException">Input starts with <c>{</c> but is not a valid JSON string-to-string object.</exception>
    public Dictionary<string, string>? Convert(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        var s = value.Trim();

        // JSON object?
        if (s.StartsWith("{"))
        {
            var dict = JsonSerializer.Deserialize<Dictionary<string, string>>(s, _jsonOptions);
            return dict ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }

        // Semicolon- or comma-separated key=value pairs: "k1=v1,k2=v2" or "k1=v1;k2=v2"
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var pairs = s.Split([';', ','], StringSplitOptions.RemoveEmptyEntries);
        foreach (var pair in pairs)
        {
            var idx = pair.IndexOf('=');
            if (idx <= 0 || idx == pair.Length - 1)
                continue;
            var k = pair[..idx].Trim();
            var v = pair[(idx + 1)..].Trim();
            if (k.Length > 0)
                result[k] = v;
        }
        return result;
    }

    object? IConfigConverter.Convert(string value) => Convert(value);
}

// ---------- IReadOnlyDictionary<string,string> ----------
/// <summary>
/// Converts a config string to <c>IReadOnlyDictionary&lt;string, string&gt;</c> using the same rules as
/// <see cref="DictionaryStringStringConfigConverter"/> (JSON object or <c>k=v</c> pairs).
/// </summary>
/// <remarks>
/// Built-in <see cref="IConfigConverter"/> (registered as a singleton via <see cref="ConfigConverterAttribute"/>).
/// The DI config pipeline uses converters to turn string values into the target type, chiefly the
/// <c>Default</c> string of <c>[AppConfigValue]</c> for non-simple types; a config section that exists is bound
/// with the configuration binder instead. You never call it directly; to support another type, write your own
/// <c>[ConfigConverter(typeof(T))]</c> class implementing <see cref="IConfigConverter{T}"/>.
/// </remarks>
[ConfigConverter(typeof(IReadOnlyDictionary<string, string>))]
public sealed class ReadOnlyDictionaryStringStringConfigConverter : IConfigConverter<IReadOnlyDictionary<string, string>>
{
    /// <inheritdoc/>
    public Type TargetType => typeof(IReadOnlyDictionary<string, string>);

    private readonly DictionaryStringStringConfigConverter _converter;

    /// <summary>Created by DI.</summary>
    /// <param name="converter">The dictionary converter that does the parsing.</param>
    public ReadOnlyDictionaryStringStringConfigConverter(DictionaryStringStringConfigConverter converter)
    {
        _converter = converter;
    }

    /// <summary>Parses <paramref name="value"/> like <see cref="DictionaryStringStringConfigConverter.Convert(string)"/>; never returns null.</summary>
    /// <param name="value">Raw config string.</param>
    public IReadOnlyDictionary<string, string>? Convert(string value)
    {
        var dict = _converter.Convert(value);
        // Return as read-only view
        return dict ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    }

    object? IConfigConverter.Convert(string value) => Convert(value);
}
