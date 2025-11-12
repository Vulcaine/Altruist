// Altruist/ConfigValueAttribute.cs
using Microsoft.Extensions.DependencyInjection;

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
    /// <summary>
    /// Optional service type to register this configuration as (defaults to the implementation type).
    /// </summary>
    public Type? ServiceType { get; }

    /// <summary>
    /// Chosen service lifetime for registration (defaults to Singleton).
    /// </summary>
    public ServiceLifetime Lifetime { get; }

    /// <summary>
    /// Execution order. Lower runs earlier; higher runs later.
    /// Defaults to 0. Use <see cref="int.MaxValue"/> to force last.
    /// </summary>
    public int Order { get; }

    /// <param name="serviceType">Optional abstraction/interface to register this config as.</param>
    /// <param name="lifetime">Service lifetime (Singleton by default).</param>
    /// <param name="order">Execution order; higher runs later. Defaults to 0.</param>
    public AppConfigurationAttribute(Type? serviceType = null, ServiceLifetime lifetime = ServiceLifetime.Singleton, int order = 0)
    {
        ServiceType = serviceType;
        Lifetime = lifetime;
        Order = order;
    }
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
