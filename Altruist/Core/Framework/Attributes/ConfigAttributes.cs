// Altruist/ConfigValueAttribute.cs
using System.Globalization;
using System.Numerics;
using System.Text.Json;

namespace Altruist;

/// <summary>
/// Marks a constructor parameter or a settable property to be resolved from IConfiguration.
/// Example: [ConfigValue("altruist:game:partitioner2d:partitionWidth", "64")]
/// </summary>
[AttributeUsage(AttributeTargets.Parameter | AttributeTargets.Property, AllowMultiple = false, Inherited = false)]
public sealed class ConfigValueAttribute : Attribute
{
    /// <summary>Configuration path (e.g., "altruist:game:partitioner2d:partitionWidth").</summary>
    public string Path { get; }

    /// <summary>Optional default value (string). Will be converted to the target type if config is missing.</summary>
    public string? Default { get; }

    public ConfigValueAttribute(string path, string? @default = null)
    {
        Path = path ?? throw new ArgumentNullException(nameof(path));
        Default = @default;
    }
}


[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
public sealed class ConfigConverterAttribute : Attribute
{
    public Type TargetType { get; }
    public ConfigConverterAttribute(Type targetType) => TargetType = targetType ?? throw new ArgumentNullException(nameof(targetType));
}

public interface IConfigConverter
{
    Type TargetType { get; }
    object? Convert(string value);
}

public interface IConfigConverter<T> : IConfigConverter
{
    new T? Convert(string value);
}

[ConfigConverter(typeof(Vector2))]
public sealed class Vector2ConfigConverter : IConfigConverter<Vector2>
{
    public Type TargetType => typeof(Vector2);

    public Vector2 Convert(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return default;

        var s = value.Trim();
        if (s.StartsWith("{") || s.StartsWith("["))
        {
            var dto = JsonSerializer.Deserialize<Vector2Dto>(s, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                      ?? throw new FormatException("Invalid Vector2 JSON default.");
            return new Vector2(dto.X, dto.Y);
        }

        var parts = s.Split(',');
        if (parts.Length != 2) throw new FormatException("Vector2 must be 'x,y' or JSON object.");
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

    public Vector3 Convert(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return default;

        var s = value.Trim();
        if (s.StartsWith("{") || s.StartsWith("["))
        {
            var dto = JsonSerializer.Deserialize<Vector3Dto>(s, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                      ?? throw new FormatException("Invalid Vector3 JSON default.");
            return new Vector3(dto.X, dto.Y, dto.Z);
        }

        var parts = s.Split(',');
        if (parts.Length != 3) throw new FormatException("Vector3 must be 'x,y,z' or JSON object.");
        return new Vector3(
            float.Parse(parts[0].Trim(), CultureInfo.InvariantCulture),
            float.Parse(parts[1].Trim(), CultureInfo.InvariantCulture),
            float.Parse(parts[2].Trim(), CultureInfo.InvariantCulture)
        );
    }

    object? IConfigConverter.Convert(string value) => Convert(value);

    private sealed class Vector3Dto { public float X { get; set; } public float Y { get; set; } public float Z { get; set; } }
}