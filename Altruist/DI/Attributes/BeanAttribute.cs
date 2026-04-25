using Microsoft.Extensions.DependencyInjection;

namespace Altruist;

[AttributeUsage(AttributeTargets.Method, AllowMultiple = false, Inherited = true)]
public sealed class BeanAttribute : Attribute
{
    public string? Name { get; }
    public ServiceLifetime Lifetime { get; set; } = ServiceLifetime.Singleton;
    public bool Replace { get; set; }
    public Type? ServiceType { get; set; }

    public BeanAttribute()
    {
    }

    public BeanAttribute(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Bean name cannot be null or whitespace.", nameof(name));

        Name = name;
    }
}
