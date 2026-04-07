using Microsoft.Extensions.DependencyInjection;

namespace Altruist;

[AttributeUsage(AttributeTargets.Class, AllowMultiple = true)]
public class ServiceAttribute : Attribute
{
    /// <summary>
    /// Optional abstraction this implementation should be registered as.
    /// If null, the implementation type itself is used.
    /// </summary>
    public Type? ServiceType { get; }

    /// <summary>
    /// DI lifetime for the service.
    /// </summary>
    public ServiceLifetime Lifetime { get; }

    /// <summary>
    /// Optional configuration types (IAltruistConfiguration) this service depends on.
    /// These configurations must have completed Configure() (IsConfigured == true)
    /// before this service is registered.
    /// </summary>
    public Type[] DependsOn { get; set; } = Array.Empty<Type>();

    public ServiceAttribute(Type? serviceType = null, ServiceLifetime lifetime = ServiceLifetime.Singleton)
    {
        ServiceType = serviceType;
        Lifetime = lifetime;
    }
}
