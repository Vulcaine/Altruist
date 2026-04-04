using System.Text.Json;

namespace Altruist;

// ---------- List<string> ----------
[ConfigConverter(typeof(List<string>))]
public sealed class ListStringConfigConverter : IConfigConverter<List<string>>
{
    public Type TargetType => typeof(List<string>);

    private readonly JsonSerializerOptions _jsonOptions;

    public ListStringConfigConverter(JsonSerializerOptions options)
    {
        _jsonOptions = options;
    }

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
[ConfigConverter(typeof(IEnumerable<string>))]
public sealed class EnumerableStringConfigConverter : IConfigConverter<IEnumerable<string>>
{
    public Type TargetType => typeof(IEnumerable<string>);

    private readonly ListStringConfigConverter _converter;

    public EnumerableStringConfigConverter(ListStringConfigConverter converter)
    {
        _converter = converter;
    }

    public IEnumerable<string>? Convert(string value)
    {
        var list = _converter.Convert(value);
        return list ?? Enumerable.Empty<string>();
    }

    object? IConfigConverter.Convert(string value) => Convert(value);
}

// ---------- IReadOnlyList<string> ----------
[ConfigConverter(typeof(IReadOnlyList<string>))]
public sealed class ReadOnlyListStringConfigConverter : IConfigConverter<IReadOnlyList<string>>
{
    public Type TargetType => typeof(IReadOnlyList<string>);

    private readonly ListStringConfigConverter _converter;

    public ReadOnlyListStringConfigConverter(ListStringConfigConverter converter)
    {
        _converter = converter;
    }

    public IReadOnlyList<string>? Convert(string value)
    {
        var list = _converter.Convert(value);
        return list ?? [];
    }

    object? IConfigConverter.Convert(string value) => Convert(value);
}

// ---------- Dictionary<string,string> ----------
[ConfigConverter(typeof(Dictionary<string, string>))]
public sealed class DictionaryStringStringConfigConverter : IConfigConverter<Dictionary<string, string>>
{
    public Type TargetType => typeof(Dictionary<string, string>);

    private readonly JsonSerializerOptions _jsonOptions;

    public DictionaryStringStringConfigConverter(JsonSerializerOptions options) => _jsonOptions = options;

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
[ConfigConverter(typeof(IReadOnlyDictionary<string, string>))]
public sealed class ReadOnlyDictionaryStringStringConfigConverter : IConfigConverter<IReadOnlyDictionary<string, string>>
{
    public Type TargetType => typeof(IReadOnlyDictionary<string, string>);

    private readonly DictionaryStringStringConfigConverter _converter;

    public ReadOnlyDictionaryStringStringConfigConverter(DictionaryStringStringConfigConverter converter)
    {
        _converter = converter;
    }

    public IReadOnlyDictionary<string, string>? Convert(string value)
    {
        var dict = _converter.Convert(value);
        // Return as read-only view
        return dict ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    }

    object? IConfigConverter.Convert(string value) => Convert(value);
}
