using Microsoft.Extensions.DependencyInjection;

namespace Altruist;

/// <summary>
/// Marks a public (static or instance) method as a factory whose return value is registered as a DI service.
/// Discovered at startup on every non-abstract class of the loaded assemblies by <see cref="AltruistDIServiceConfig"/>
/// (bean methods are registered before <see cref="ServiceAttribute"/> classes).
/// </summary>
/// <remarks>
/// <para>Use a bean when the instance must be built by hand: third-party types you cannot annotate with
/// <see cref="ServiceAttribute"/>, or objects that need custom construction logic. For your own classes prefer
/// <see cref="ServiceAttribute"/>.</para>
/// <para>Method parameters are resolved like constructor parameters (DI services, <see cref="AppConfigValueAttribute"/>,
/// <see cref="ServiceKeyAttribute"/>, collections, <see cref="Lazy{T}"/>). For an instance method the declaring class is
/// constructed through <see cref="DependencyResolver"/> each time the factory runs (it does not need to be a service itself).
/// The method runs lazily, when the service is first resolved, and must not return null.</para>
/// <para>Open generic methods and methods returning <c>void</c>/<see cref="Task"/>/<see cref="ValueTask"/> are skipped with a warning.
/// The method's return type must be assignable to <see cref="ServiceType"/> or startup fails.</para>
/// </remarks>
/// <example>
/// <code>
/// public sealed class JsonBeans
/// {
///     [Bean]
///     public static JsonSerializerOptions JsonOptions() =&gt; new() { PropertyNameCaseInsensitive = true };
///
///     [Bean("audit", Lifetime = ServiceLifetime.Scoped, ServiceType = typeof(IEventSink))]
///     public FileEventSink AuditSink([AppConfigValue("myapp:audit:path")] string path) =&gt; new(path);
/// }
/// // inject with: MyService([ServiceKey("audit")] IEventSink sink)
/// </code>
/// </example>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false, Inherited = true)]
public sealed class BeanAttribute : Attribute
{
    /// <summary>
    /// Optional service key. When set, the bean is registered as a keyed service (resolve it with
    /// <see cref="ServiceKeyAttribute"/> or <c>GetRequiredKeyedService</c>); when null it is a normal registration.
    /// </summary>
    public string? Name { get; }
    /// <summary>DI lifetime of the produced service; defaults to <see cref="ServiceLifetime.Singleton"/>.</summary>
    public ServiceLifetime Lifetime { get; set; } = ServiceLifetime.Singleton;
    /// <summary>
    /// When true, existing registrations of <see cref="ServiceType"/> with the same key (or unkeyed, when <see cref="Name"/>
    /// is null) are removed before this bean is added, so the bean overrides them. Only registrations already present at
    /// that point are removed; when false the bean is added alongside them (the last registration wins for single resolution).
    /// </summary>
    public bool Replace { get; set; }
    /// <summary>Type to register the bean as; defaults to the method's return type.</summary>
    public Type? ServiceType { get; set; }

    /// <summary>Registers an unkeyed bean.</summary>
    public BeanAttribute()
    {
    }

    /// <summary>Registers a keyed bean.</summary>
    /// <param name="name">Service key; must not be null or whitespace.</param>
    /// <exception cref="ArgumentException"><paramref name="name"/> is null or whitespace.</exception>
    public BeanAttribute(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Bean name cannot be null or whitespace.", nameof(name));

        Name = name;
    }
}
