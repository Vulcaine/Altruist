using Microsoft.Extensions.DependencyInjection;

namespace Altruist;

/// <summary>
/// Registers the annotated class in the DI container. At startup every non-abstract class carrying this
/// attribute (in any loaded assembly) is discovered by <see cref="AltruistDIServiceConfig"/> and registered
/// under its own type, plus a forwarding registration under <see cref="ServiceType"/> when one is given
/// (both share the same instance for singletons). Constructor dependencies are planned and registered first
/// (<see cref="DependencyPlanner"/>), and instances are built by <see cref="DependencyResolver"/>, which fills
/// <see cref="AppConfigValueAttribute"/> / <see cref="ServiceKeyAttribute"/> parameters and properties.
/// </summary>
/// <remarks>
/// <para>Apply the attribute several times to expose one implementation under several abstractions.</para>
/// <para>Combine with <see cref="ConditionalOnConfigAttribute"/> to register only when a config key exists / matches
/// (or once per item of a config list via <see cref="ConditionalOnConfigAttribute.KeyField"/>), and with
/// <see cref="ConditionalOnMissingServiceAttribute"/> for a replaceable framework default.</para>
/// <para>Use <see cref="BeanAttribute"/> instead when the instance must be built by hand in a factory method
/// (third-party types, types you cannot annotate). Use <see cref="ServiceConfigurationAttribute"/> for a class
/// that adds registrations to the <see cref="Microsoft.Extensions.DependencyInjection.IServiceCollection"/>
/// itself rather than being a service. Use <see cref="ConfigurationPropertiesAttribute"/> for a plain
/// config POCO bound from a section.</para>
/// <para>A class with no public constructor is skipped. Simple-typed constructor parameters (numbers, strings,
/// enums...) must carry <see cref="AppConfigValueAttribute"/> or a default value, or startup fails.</para>
/// </remarks>
/// <example>
/// <code>
/// [Service(typeof(IScoreService))]                       // singleton, injectable as IScoreService and ScoreService
/// public sealed class ScoreService : IScoreService
/// {
///     public ScoreService(ILogger&lt;ScoreService&gt; log,
///         [AppConfigValue("myapp:score:max", "100")] int max) { ... }
/// }
///
/// [Service(typeof(IClock), ServiceLifetime.Transient)]
/// public sealed class SystemClock : IClock { ... }
/// </code>
/// </example>
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
    /// Optional configuration types (<see cref="Altruist.Contracts.IAltruistConfiguration"/>) this service depends on.
    /// Each is registered and its <c>Configure</c> run synchronously (once per service collection) before this
    /// service is registered, so registrations it adds are visible to this service's dependency planning.
    /// Types that do not implement <see cref="Altruist.Contracts.IAltruistConfiguration"/> are ignored with a warning.
    /// </summary>
    public Type[] DependsOn { get; set; } = Array.Empty<Type>();

    /// <summary>Marks the class as a DI service.</summary>
    /// <param name="serviceType">Abstraction to register as (in addition to the implementation type); null registers only the implementation type.</param>
    /// <param name="lifetime">DI lifetime; defaults to <see cref="ServiceLifetime.Singleton"/>.</param>
    public ServiceAttribute(Type? serviceType = null, ServiceLifetime lifetime = ServiceLifetime.Singleton)
    {
        ServiceType = serviceType;
        Lifetime = lifetime;
    }
}
