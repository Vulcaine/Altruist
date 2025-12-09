using System.Globalization;
using System.Numerics;
using System.Text.Json;

namespace Altruist;


[ConfigConverter(typeof(Vector2))]
public sealed class Vector2ConfigConverter : IConfigConverter<Vector2>
{
    public Type TargetType => typeof(Vector2);

    private readonly JsonSerializerOptions _jsonOptions;

    public Vector2ConfigConverter(JsonSerializerOptions options) => _jsonOptions = options;

    public Vector2 Convert(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return default;

        var s = value.Trim();
        if (s.StartsWith("{") || s.StartsWith("["))
        {
            var dto = JsonSerializer.Deserialize<Vector2Dto>(s, _jsonOptions) ?? throw new FormatException("Invalid Vector2 JSON default.");
            return new Vector2(dto.X, dto.Y);
        }

        var parts = s.Split(',');
        if (parts.Length != 2)
            throw new FormatException("Vector2 must be 'x,y' or JSON object.");
        return new Vector2(
            float.Parse(parts[0].Trim(), CultureInfo.InvariantCulture),
            float.Parse(parts[1].Trim(), CultureInfo.InvariantCulture)
        );
    }

    object? IConfigConverter.Convert(string value) => Convert(value);

    private sealed class Vector2Dto { public float X { get; set; } public float Y { get; set; } }
}

[ConfigConverter(typeof(Vector3))]
public sealed class Vector3ConfigConverter : IConfigConverter<Vector3>
{
    public Type TargetType => typeof(Vector3);

    private readonly JsonSerializerOptions _jsonOptions;

    public Vector3ConfigConverter(JsonSerializerOptions options) => _jsonOptions = options;

    public Vector3 Convert(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return default;

        var s = value.Trim();
        if (s.StartsWith("{") || s.StartsWith("["))
        {
            var dto = JsonSerializer.Deserialize<Vector3Dto>(s, _jsonOptions)
                      ?? throw new FormatException("Invalid Vector3 JSON default.");
            return new Vector3(dto.X, dto.Y, dto.Z);
        }

        var parts = s.Split(',');
        if (parts.Length != 3)
            throw new FormatException("Vector3 must be 'x,y,z' or JSON object.");
        return new Vector3(
            float.Parse(parts[0].Trim(), CultureInfo.InvariantCulture),
            float.Parse(parts[1].Trim(), CultureInfo.InvariantCulture),
            float.Parse(parts[2].Trim(), CultureInfo.InvariantCulture)
        );
    }

    object? IConfigConverter.Convert(string value) => Convert(value);

    private sealed class Vector3Dto { public float X { get; set; } public float Y { get; set; } public float Z { get; set; } }
}
