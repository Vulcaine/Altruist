// Altruist/ConfigValueAttribute.cs
using System.Numerics;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Primitives;

namespace Altruist;

/// <summary>
/// Marks a class implementing <see cref="Altruist.Contracts.IAltruistConfiguration"/> as a startup configuration step:
/// it is registered in DI (constructor parameters, including <see cref="AppConfigValueAttribute"/> ones, are resolved like
/// a service) and then its <c>Configure(IServiceCollection)</c> is called once, so it can add registrations by hand
/// (options, authentication, hosted services, third-party integrations).
/// </summary>
/// <remarks>
/// <para>Discovered and run by <see cref="ConfigAttributeConfiguration"/> after all <see cref="ServiceAttribute"/> classes
/// and beans are registered. All discovered steps are registered first, then each <c>Configure</c> runs in ascending
/// <see cref="Order"/> (ties broken by full type name). Each instance is created from a temporary service provider built
/// at that moment, so it sees registrations made so far. Classes that do not implement
/// <see cref="Altruist.Contracts.IAltruistConfiguration"/> are ignored. <see cref="ConditionalOnConfigAttribute"/> (gate
/// mode) is honoured. A step whose service type is already registered is not registered again.</para>
/// <para>Use <see cref="ServiceAttribute"/> for an ordinary service; use this only when you need to manipulate the
/// <see cref="IServiceCollection"/> directly. A service that needs a step to have run first can declare it with
/// <see cref="ServiceAttribute.DependsOn"/>.</para>
/// </remarks>
/// <example>
/// <code>
/// [ServiceConfiguration(order: 10)]
/// public sealed class MailSetup : IAltruistConfiguration
/// {
///     private readonly string _host;
///     public MailSetup([AppConfigValue("myapp:mail:host", "localhost")] string host) =&gt; _host = host;
///     public bool IsConfigured { get; set; }
///     public Task Configure(IServiceCollection services)
///     {
///         services.AddSingleton(new SmtpClient(_host));
///         return Task.CompletedTask;
///     }
/// }
/// </code>
/// </example>
[AttributeUsage(AttributeTargets.Class, Inherited = false, AllowMultiple = false)]
public sealed class ServiceConfigurationAttribute : Attribute
{
    /// <summary>Type the configuration step is registered as; null means its own type.</summary>
    public Type? ServiceType { get; }

    /// <summary>DI lifetime of the configuration step's registration (default singleton).</summary>
    public ServiceLifetime Lifetime { get; }

    /// <summary>Execution order of <c>Configure</c>; lower runs first. The framework's HTTP startup step uses <see cref="int.MaxValue"/>.</summary>
    public int Order { get; }

    /// <summary>Marks the class as a configuration step.</summary>
    /// <param name="serviceType">Registration type; null for the class itself.</param>
    /// <param name="lifetime">Registration lifetime.</param>
    /// <param name="order">Execution order (ascending).</param>
    public ServiceConfigurationAttribute(Type? serviceType = null, ServiceLifetime lifetime = ServiceLifetime.Singleton, int order = 0)
    {
        ServiceType = serviceType;
        Lifetime = lifetime;
        Order = order;
    }
}


/// <summary>
/// Marker for configuration classes implementing <see cref="Altruist.Contracts.IAltruistConfiguration"/>.
/// </summary>
/// <remarks>
/// Currently not read by any discovery code: <see cref="ConfigAttributeConfiguration"/> discovers steps via
/// <see cref="ServiceConfigurationAttribute"/>, which is what you should use.
/// </remarks>
[AttributeUsage(AttributeTargets.Class, Inherited = false, AllowMultiple = false)]
public sealed class AppConfigurationAttribute : Attribute
{
}
/// <summary>
/// Injects a configuration value into a constructor parameter, a <see cref="BeanAttribute"/> /
/// <see cref="PostConstructAttribute"/> method parameter, or a public settable property (set right after construction)
/// of a class built by the Altruist DI (<see cref="ServiceAttribute"/>, <see cref="ServiceConfigurationAttribute"/>, beans).
/// </summary>
/// <remarks>
/// <para>Conversion: simple types (numbers, bool, string, enum (case-insensitive), <see cref="Guid"/>, <see cref="TimeSpan"/>,
/// <see cref="DateTime"/>, <see cref="DateTimeOffset"/>) are parsed with the invariant culture (number suffixes like
/// <c>f</c>/<c>d</c>/<c>u</c>/<c>l</c> and <c>_</c> separators are tolerated); a registered <see cref="IConfigConverter{T}"/>
/// for the type takes precedence. A complex type is converted by its converter when the key holds a single value, otherwise
/// bound from the section with <c>ConfigurationBinder.Bind</c> (it needs a parameterless constructor); its
/// <see cref="Default"/> is parsed by a converter or as JSON.</para>
/// <para>Missing key: <see cref="Default"/> is used if given; otherwise null for reference/nullable types; a non-nullable value
/// type with neither fails startup.</para>
/// <para>Live values: declare the parameter as <see cref="ILiveConfigValue{T}"/> to receive a wrapper that re-reads the key on
/// configuration reload and raises <see cref="ILiveConfigValue{T}.OnChange"/>.</para>
/// <para>Wildcards: a path starting with <c>*</c> (e.g. <c>"*:gravity"</c>) is resolved relative to the current item of a
/// list-style <see cref="ConditionalOnConfigAttribute"/> (see <see cref="ConditionalOnConfigAttribute.KeyField"/>).</para>
/// <para>Use this for individual values; for a whole typed section use <see cref="ConfigurationPropertiesAttribute"/>.
/// Parameters of simple types without this attribute must have a default value, or startup fails.</para>
/// </remarks>
/// <example>
/// <code>
/// public Matchmaker(
///     [AppConfigValue("myapp:match:size", "4")] int teamSize,
///     [AppConfigValue("myapp:match:timeout", "00:00:30")] TimeSpan timeout,
///     [AppConfigValue("myapp:match:region")] string? region,
///     [AppConfigValue("myapp:match:tickRate", "60")] ILiveConfigValue&lt;int&gt; tickRate) { ... }
/// </code>
/// </example>
[AttributeUsage(AttributeTargets.Parameter | AttributeTargets.Property, AllowMultiple = false, Inherited = false)]
public sealed class AppConfigValueAttribute : Attribute
{
    /// <summary>Colon-separated configuration path (e.g. <c>altruist:server:http:port</c>); may start with <c>*</c> for list items.</summary>
    public string Path { get; }

    /// <summary>Optional default (as a string), converted to the target type when the key is missing; for an
    /// <see cref="ILiveConfigValue{T}"/> target it is the value while the key is missing.</summary>
    public string? Default { get; }

    /// <summary>Binds the member to the configuration key at <paramref name="path"/>.</summary>
    /// <param name="path">Configuration path.</param>
    /// <param name="default">Value used when the key is missing.</param>
    public AppConfigValueAttribute(string path, string? @default = null)
    {
        Path = path ?? throw new ArgumentNullException(nameof(path));
        Default = @default;
    }
}


/// <summary>
/// Registers a custom string-to-type converter used by <see cref="AppConfigValueAttribute"/> conversion. The class must
/// implement <see cref="IConfigConverter"/> (typically <see cref="IConfigConverter{T}"/>); it is also registered as a
/// singleton service under its own type.
/// </summary>
/// <remarks>
/// Converters are discovered once at startup (before services are built); their constructor dependencies are resolved
/// from the services registered so far. A converter is used for every scalar value of its target type (a key holding a
/// string, e.g. <c>color: red</c>, simple or complex type) and for <see cref="AppConfigValueAttribute.Default"/> strings;
/// a section with child keys of a complex type is bound with the standard configuration binder instead. One converter
/// per target type (the last discovered wins).
/// </remarks>
/// <example>
/// <code>
/// [ConfigConverter(typeof(Color))]
/// public sealed class ColorConverter : IConfigConverter&lt;Color&gt;
/// {
///     public Type TargetType =&gt; typeof(Color);
///     public Color Convert(string value) =&gt; Color.Parse(value);
///     object? IConfigConverter.Convert(string value) =&gt; Convert(value);
/// }
/// </code>
/// </example>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
public sealed class ConfigConverterAttribute : ServiceAttribute
{
    /// <summary>The type this converter produces.</summary>
    public Type TargetType { get; }
    // Registered as the converter type itself: registering it under TargetType (e.g. List<string>)
    // put a converter instance behind a service type it does not implement, which breaks
    // ServiceProvider validation (WebApplication validates on build in Development).
    /// <summary>Declares the converter's target type.</summary>
    /// <param name="targetType">Type produced by the converter.</param>
    public ConfigConverterAttribute(Type targetType) : base(null, lifetime: ServiceLifetime.Singleton) => TargetType = targetType ?? throw new ArgumentNullException(nameof(targetType));
}

/// <summary>
/// Non-generic converter contract from a raw configuration string to <see cref="TargetType"/>.
/// Implement <see cref="IConfigConverter{T}"/> and mark the class with <see cref="ConfigConverterAttribute"/>.
/// </summary>
public interface IConfigConverter
{
    /// <summary>The type produced by <see cref="Convert"/>.</summary>
    Type TargetType { get; }

    /// <summary>Converts the raw configuration string.</summary>
    /// <param name="value">Raw value from configuration or an attribute default.</param>
    /// <returns>The converted value (boxed).</returns>
    object? Convert(string value);
}

/// <summary>Typed configuration converter; see <see cref="ConfigConverterAttribute"/>.</summary>
/// <typeparam name="T">Produced type.</typeparam>
public interface IConfigConverter<T> : IConfigConverter
{
    /// <summary>Converts the raw configuration string to <typeparamref name="T"/>.</summary>
    /// <param name="value">Raw value.</param>
    new T? Convert(string value);
}

/// <summary>
/// A configuration value that follows configuration reloads (YAML file edits, runtime overrides). Obtain it by declaring a
/// constructor parameter of this type with <see cref="AppConfigValueAttribute"/>; the framework supplies a
/// <see cref="LiveConfigValue{T}"/>.
/// </summary>
/// <remarks>
/// Use this for tunables that may change while the server runs; for values read once use a plain
/// <see cref="AppConfigValueAttribute"/> parameter. <see cref="OnChange"/> is raised on the configuration reload
/// thread (not necessarily your simulation / request thread) for every reload, even if the value did not change.
/// Missing keys yield <c>default</c>. <see cref="LiveConfigSugarExtensions.BindTo{T}"/> is the usual way to consume it.
/// </remarks>
/// <typeparam name="T">Value type; <see cref="Vector2"/>/<see cref="Vector3"/> (and nullable) read <c>x</c>/<c>y</c>/<c>z</c> children; other types use the configuration binder.</typeparam>
public interface ILiveConfigValue<T>
{
    /// <summary>The latest value (default when the key is missing).</summary>
    T? Current { get; }

    /// <summary>Raised after each configuration reload with the newly read value.</summary>
    event Action<T?> OnChange;
}

/// <summary>
/// Default <see cref="ILiveConfigValue{T}"/> implementation: reads a key and re-reads it whenever the configuration's
/// reload token fires. Registers its key in <see cref="LiveConfigRegistry"/>. Normally created by the DI resolver, not by hand.
/// </summary>
/// <typeparam name="T">Value type.</typeparam>
public sealed class LiveConfigValue<T> : ILiveConfigValue<T>
{
    private readonly IConfiguration config;
    private readonly string key;
    private readonly T fallback;

    /// <inheritdoc/>
    public T Current { get; private set; }

    /// <inheritdoc/>
    public event Action<T>? OnChange;

    /// <summary>Tracks <paramref name="key"/> and registers it under the same name.</summary>
    /// <param name="config">Configuration to read and watch.</param>
    /// <param name="key">Key to read.</param>
    public LiveConfigValue(IConfiguration config, string key)
        : this(config, key, key)
    {
    }

    /// <summary>Tracks <paramref name="key"/> and registers <paramref name="registryKey"/> (the absolute path) as live.</summary>
    /// <param name="config">Configuration to read and watch (may be an item section).</param>
    /// <param name="key">Key to read, relative to <paramref name="config"/>.</param>
    /// <param name="registryKey">Absolute path recorded in <see cref="LiveConfigRegistry"/>.</param>
    public LiveConfigValue(IConfiguration config, string key, string registryKey)
        : this(config, key, registryKey, default!)
    {
    }

    /// <summary>
    /// Tracks <paramref name="key"/> and registers <paramref name="registryKey"/> as live; <see cref="Current"/> is
    /// <paramref name="fallback"/> while the key is missing (initially or after a reload removed it).
    /// </summary>
    /// <param name="config">Configuration to read and watch (may be an item section).</param>
    /// <param name="key">Key to read, relative to <paramref name="config"/>.</param>
    /// <param name="registryKey">Absolute path recorded in <see cref="LiveConfigRegistry"/>.</param>
    /// <param name="fallback">Value used while the key is missing (the <see cref="AppConfigValueAttribute.Default"/>).</param>
    public LiveConfigValue(IConfiguration config, string key, string registryKey, T fallback)
    {
        this.config = config;
        this.key = key;
        this.fallback = fallback;

        LiveConfigRegistry.Register(registryKey);

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
            return fallback;

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

/// <summary>Convenience helpers for <see cref="ILiveConfigValue{T}"/>.</summary>
public static class LiveConfigSugarExtensions
{

    /// <summary>
    /// Calls <paramref name="setter"/> with the current value immediately and again on every change.
    /// The subscription is never removed, so the target lives as long as the live value.
    /// </summary>
    /// <typeparam name="T">Value type.</typeparam>
    /// <param name="live">The live value.</param>
    /// <param name="setter">Receives the value (on the configuration reload thread for changes).</param>
    /// <example>
    /// <code>
    /// public Ticker([AppConfigValue("myapp:tick:rate", "60")] ILiveConfigValue&lt;int&gt; rate)
    ///     =&gt; rate.BindTo(v =&gt; _rate = v);
    /// </code>
    /// </example>
    public static void BindTo<T>(
        this ILiveConfigValue<T> live,
        Action<T?> setter)
    {
        setter(live.Current);
        live.OnChange += setter;
    }
}

/// <summary>
/// In-memory configuration provider whose <see cref="Set"/> immediately triggers a configuration reload
/// (so <see cref="ILiveConfigValue{T}"/> subscribers are notified). Registered as a singleton service.
/// </summary>
[Service]
public sealed class MutableConfigProvider : ConfigurationProvider
{
    /// <summary>Sets <paramref name="key"/> to <paramref name="value"/> and signals a reload.</summary>
    /// <param name="key">Configuration path.</param>
    /// <param name="value">New value.</param>
    public override void Set(string key, string? value)
    {
        Data[key] = value;
        OnReload();
    }
}

/// <summary>
/// Configuration source inserted first into the web host's configuration at startup (by
/// <c>AltruistStartupConfiguration</c>). Registered as a singleton service.
/// </summary>
/// <remarks>
/// <see cref="Build"/> returns <see cref="Provider"/> itself, so values set on the DI-registered
/// <see cref="MutableConfigProvider"/> reach every configuration built from this source.
/// </remarks>
[Service]
public sealed class MutableConfigSource : IConfigurationSource
{
    /// <summary>The DI-registered provider instance injected at construction.</summary>
    public readonly MutableConfigProvider Provider;

    /// <summary>Creates the source around the DI-registered provider.</summary>
    /// <param name="mutableConfigProvider">Singleton provider.</param>
    public MutableConfigSource(MutableConfigProvider mutableConfigProvider)
    {
        Provider = mutableConfigProvider;
    }

    /// <inheritdoc/>
    public IConfigurationProvider Build(IConfigurationBuilder builder)
        => Provider;
}

/// <summary>
/// Process-wide set of configuration paths that are consumed through <see cref="ILiveConfigValue{T}"/>, so tools can tell
/// which keys take effect without a restart. Not thread-safe; registration happens during service construction.
/// </summary>
public static class LiveConfigRegistry
{
    private static readonly HashSet<string> _keys = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Records <paramref name="key"/> as live (case-insensitive).</summary>
    /// <param name="key">Absolute configuration path.</param>
    public static void Register(string key) => _keys.Add(key);

    /// <summary>True if <paramref name="key"/> equals a registered live key or is nested under one (<c>gravity:x</c> under <c>gravity</c>).</summary>
    /// <param name="key">Configuration path to test.</param>
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
