using System.Globalization;
using System.Numerics;
using System.Text.Json;

namespace Altruist;


/// <summary>
/// Converts a config string to <see cref="Vector2"/>: <c>"x,y"</c> (e.g. <c>"1.5, -2"</c>) or a JSON object
/// <c>{"X":1.5,"Y":-2}</c>. Blank input yields <see cref="Vector2.Zero"/>.
/// </summary>
/// <remarks>
/// Built-in <see cref="IConfigConverter"/> (registered as a singleton via <see cref="ConfigConverterAttribute"/>),
/// used by the DI config pipeline chiefly for the <c>Default</c> string of <c>[AppConfigValue]</c>. Numbers are
/// parsed with the invariant culture (use <c>.</c> as the decimal separator).
/// </remarks>
/// <example>
/// <code>
/// public Spawner([AppConfigValue("myapp:spawn:offset", "0,1")] Vector2 offset) { }
/// </code>
/// </example>
[ConfigConverter(typeof(Vector2))]
public sealed class Vector2ConfigConverter : IConfigConverter<Vector2>
{
    /// <inheritdoc/>
    public Type TargetType => typeof(Vector2);

    private readonly JsonSerializerOptions _jsonOptions;

    /// <summary>Created by DI with the framework's shared JSON options.</summary>
    /// <param name="options">JSON options used for the JSON-object form (the framework registers case-insensitive options, so <c>x</c> and <c>X</c> both match).</param>
    public Vector2ConfigConverter(JsonSerializerOptions options) => _jsonOptions = options;

    /// <summary>Parses <paramref name="value"/> as <c>"x,y"</c> or a JSON object.</summary>
    /// <param name="value">Raw config string.</param>
    /// <exception cref="FormatException">Not exactly two comma-separated numbers, or a number is malformed.</exception>
    /// <exception cref="JsonException">Input starts with <c>{</c> or <c>[</c> but is not a valid JSON object.</exception>
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

/// <summary>
/// Converts a config string to <see cref="Vector3"/>: <c>"x,y,z"</c> (e.g. <c>"0, 1.5, 0"</c>) or a JSON object
/// <c>{"X":0,"Y":1.5,"Z":0}</c>. Blank input yields <see cref="Vector3.Zero"/>.
/// </summary>
/// <remarks>
/// Built-in <see cref="IConfigConverter"/> (registered as a singleton via <see cref="ConfigConverterAttribute"/>),
/// used by the DI config pipeline chiefly for the <c>Default</c> string of <c>[AppConfigValue]</c>. Numbers are
/// parsed with the invariant culture (use <c>.</c> as the decimal separator).
/// </remarks>
[ConfigConverter(typeof(Vector3))]
public sealed class Vector3ConfigConverter : IConfigConverter<Vector3>
{
    /// <inheritdoc/>
    public Type TargetType => typeof(Vector3);

    private readonly JsonSerializerOptions _jsonOptions;

    /// <summary>Created by DI with the framework's shared JSON options.</summary>
    /// <param name="options">JSON options used for the JSON-object form (the framework registers case-insensitive options, so <c>x</c> and <c>X</c> both match).</param>
    public Vector3ConfigConverter(JsonSerializerOptions options) => _jsonOptions = options;

    /// <summary>Parses <paramref name="value"/> as <c>"x,y,z"</c> or a JSON object.</summary>
    /// <param name="value">Raw config string.</param>
    /// <exception cref="FormatException">Not exactly three comma-separated numbers, or a number is malformed.</exception>
    /// <exception cref="JsonException">Input starts with <c>{</c> or <c>[</c> but is not a valid JSON object.</exception>
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
