// Altruist/ConfigValueAttribute.cs
using System.Numerics;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Primitives;

namespace Altruist;

[AttributeUsage(AttributeTargets.Class, Inherited = false, AllowMultiple = false)]
public sealed class ServiceConfigurationAttribute : Attribute
{
    public Type? ServiceType { get; }
    public ServiceLifetime Lifetime { get; }
    public int Order { get; }

    public ServiceConfigurationAttribute(Type? serviceType = null, ServiceLifetime lifetime = ServiceLifetime.Singleton, int order = 0)
    {
        ServiceType = serviceType;
        Lifetime = lifetime;
        Order = order;
    }
}


/// <summary>
/// Marks a configuration class that should be discovered and executed by
/// <see cref="ConfigAttributeConfiguration"/>.
///
/// ⚠️ May only be applied to types that implement <see cref="IAltruistConfiguration"/>.
/// </summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false, AllowMultiple = false)]
public sealed class AppConfigurationAttribute : Attribute
{
}
/// <summary>
/// Marks a constructor parameter or a settable property to be resolved from IConfiguration.
/// Example: [ConfigValue("altruist:game:partitioner2d:partitionWidth", "64")]
/// </summary>
[AttributeUsage(AttributeTargets.Parameter | AttributeTargets.Property, AllowMultiple = false, Inherited = false)]
public sealed class AppConfigValueAttribute : Attribute
{
    /// <summary>Configuration path (e.g., "altruist:game:partitioner2d:partitionWidth").</summary>
    public string Path { get; }

    /// <summary>Optional default value (string). Will be converted to the target type if config is missing.</summary>
    public string? Default { get; }

    public AppConfigValueAttribute(string path, string? @default = null)
    {
        Path = path ?? throw new ArgumentNullException(nameof(path));
        Default = @default;
    }
}


[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
public sealed class ConfigConverterAttribute : ServiceAttribute
{
    public Type TargetType { get; }
    public ConfigConverterAttribute(Type targetType) : base(targetType, lifetime: ServiceLifetime.Singleton) => TargetType = targetType ?? throw new ArgumentNullException(nameof(targetType));
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

public interface ILiveConfigValue<T>
{
    T? Current { get; }
    event Action<T?> OnChange;
}

public sealed class LiveConfigValue<T> : ILiveConfigValue<T>
{
    private readonly IConfiguration config;
    private readonly string key;

    public T Current { get; private set; }
    public event Action<T>? OnChange;

    public LiveConfigValue(IConfiguration config, string key)
    {
        this.config = config;
        this.key = key;

        LiveConfigRegistry.Register(key);

        Current = Read();
        ChangeToken.OnChange(config.GetReloadToken, Reload);
    }

    private void Reload()
    {
        Current = Read();
        OnChange?.Invoke(Current);
    }

    private T Read()
    {
        var section = config.GetSection(key);
        if (!section.Exists())
            return default!;

        if (typeof(T) == typeof(Vector2))
        {
            var v = ReadVector2(section);
            return (T)(object)v;
        }

        if (typeof(T) == typeof(Vector2?))
        {
            var v = ReadVector2(section);
            return (T)(object)(Vector2?)v;
        }

        if (typeof(T) == typeof(Vector3))
        {
            var v = ReadVector3(section);
            return (T)(object)v;
        }

        if (typeof(T) == typeof(Vector3?))
        {
            var v = ReadVector3(section);
            return (T)(object)(Vector3?)v;
        }

        var bound = section.Get<T>();
        return bound!;
    }

    private static Vector2 ReadVector2(IConfigurationSection section)
    {
        float x = section.GetValue<float>("x");
        float y = section.GetValue<float>("y");
        return new Vector2(x, y);
    }

    private static Vector3 ReadVector3(IConfigurationSection section)
    {
        float x = section.GetValue<float>("x");
        float y = section.GetValue<float>("y");
        float z = section.GetValue<float>("z");
        return new Vector3(x, y, z);
    }
}

public static class LiveConfigSugarExtensions
{

    public static void BindTo<T>(
        this ILiveConfigValue<T> live,
        Action<T?> setter)
    {
        setter(live.Current);
        live.OnChange += setter;
    }
}

[Service]
public sealed class MutableConfigProvider : ConfigurationProvider
{
    public override void Set(string key, string? value)
    {
        Data[key] = value;
        OnReload();
    }
}

[Service]
public sealed class MutableConfigSource : IConfigurationSource
{
    public readonly MutableConfigProvider Provider;

    public MutableConfigSource(MutableConfigProvider mutableConfigProvider)
    {
        Provider = mutableConfigProvider;
    }

    public IConfigurationProvider Build(IConfigurationBuilder builder)
        => new MutableConfigProvider();
}

public static class LiveConfigRegistry
{
    private static readonly HashSet<string> _keys = new(StringComparer.OrdinalIgnoreCase);

    public static void Register(string key) => _keys.Add(key);
    public static bool IsLiveConfig(string key)
    {
        foreach (var liveKey in _keys)
        {
            // exact match
            if (key.Equals(liveKey, StringComparison.OrdinalIgnoreCase))
                return true;

            // nested match → e.g. "gravity" matches "gravity:x"
            if (key.StartsWith(liveKey + ":", StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }
}
