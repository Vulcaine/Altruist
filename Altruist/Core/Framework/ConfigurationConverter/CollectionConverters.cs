using System.Text.Json;

namespace Altruist;

// ---------- List<string> ----------
[ConfigConverter(typeof(List<string>))]
public sealed class ListStringConfigConverter : IConfigConverter<List<string>>
{
    public Type TargetType => typeof(List<string>);

    public List<string>? Convert(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return new List<string>();

        var s = value.Trim();

        // JSON array?
        if (s.StartsWith("["))
            return JsonSerializer.Deserialize<List<string>>(s, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
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

    public IEnumerable<string>? Convert(string value)
    {
        // Reuse the List<string> converter behavior
        var list = new ListStringConfigConverter().Convert(value);
        return list ?? Enumerable.Empty<string>();
    }

    object? IConfigConverter.Convert(string value) => Convert(value);
}

// ---------- IReadOnlyList<string> ----------
[ConfigConverter(typeof(IReadOnlyList<string>))]
public sealed class ReadOnlyListStringConfigConverter : IConfigConverter<IReadOnlyList<string>>
{
    public Type TargetType => typeof(IReadOnlyList<string>);

    public IReadOnlyList<string>? Convert(string value)
    {
        var list = new ListStringConfigConverter().Convert(value);
        return list ?? [];
    }

    object? IConfigConverter.Convert(string value) => Convert(value);
}

// ---------- Dictionary<string,string> ----------
[ConfigConverter(typeof(Dictionary<string, string>))]
public sealed class DictionaryStringStringConfigConverter : IConfigConverter<Dictionary<string, string>>
{
    public Type TargetType => typeof(Dictionary<string, string>);

    public Dictionary<string, string>? Convert(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        var s = value.Trim();

        // JSON object?
        if (s.StartsWith("{"))
        {
            var dict = JsonSerializer.Deserialize<Dictionary<string, string>>(s, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });
            return dict ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }

        // Semicolon- or comma-separated key=value pairs: "k1=v1,k2=v2" or "k1=v1;k2=v2"
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var pairs = s.Split(new[] { ';', ',' }, StringSplitOptions.RemoveEmptyEntries);
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

    public IReadOnlyDictionary<string, string>? Convert(string value)
    {
        var dict = new DictionaryStringStringConfigConverter().Convert(value);
        // Return as read-only view
        return dict ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    }

    object? IConfigConverter.Convert(string value) => Convert(value);
}
