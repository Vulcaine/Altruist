using Microsoft.Extensions.DependencyInjection;

namespace Altruist;


[AttributeUsage(AttributeTargets.Class, AllowMultiple = true)]
public class ServiceAttribute : Attribute
{
    public Type? ServiceType { get; }
    public ServiceLifetime Lifetime { get; }

    public ServiceAttribute(Type? serviceType, ServiceLifetime lifetime = ServiceLifetime.Singleton)
    {
        ServiceType = serviceType;
        Lifetime = lifetime;
    }
}
