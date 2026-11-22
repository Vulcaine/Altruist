// Altruist/Modules/AltruistModuleAttributes.cs
namespace Altruist;

/// <summary>
/// Marks a type as an Altruist module. Must be applied to a public static class.
/// </summary>
/// <remarks>
/// <para>Modules are run by <see cref="AltruistModuleConfig.RunModulesAsync"/> once, after all services are registered,
/// the root provider is built and <see cref="PostConstructAttribute"/> hooks have run, and before the HTTP server starts.
/// Only the full server bootstrap (<see cref="AltruistApplication.Run"/>) runs modules; <see cref="AltruistDI.Run"/> does not.</para>
/// <para>Use a module for one-time startup wiring that needs several resolved services together (seeding, registering
/// handlers with a registry). For initialization of a single service, prefer a <see cref="PostConstructAttribute"/> method on it.</para>
/// </remarks>
/// <example>
/// <code>
/// [AltruistModule("Shop")]
/// public static class ShopModule
/// {
///     [AltruistModuleLoader]
///     public static async Task Load(ICatalog catalog, ILogger&lt;Catalog&gt; log) =&gt; await catalog.SeedAsync();
/// }
/// </code>
/// </example>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
public sealed class AltruistModuleAttribute : Attribute
{
    /// <summary>Display name used in logs; defaults to the type's full name.</summary>
    public string? Name { get; }

    /// <summary>Marks the static class as a module.</summary>
    /// <param name="name">Optional display name.</param>
    public AltruistModuleAttribute(string? name = null) => Name = name;
}

/// <summary>
/// Marks a public static method of an <see cref="AltruistModuleAttribute"/> class as a module loader entrypoint.
/// It must return <c>void</c> or a <see cref="System.Threading.Tasks.Task"/> (awaited); every parameter is resolved from
/// the root service provider (no <see cref="AppConfigValueAttribute"/> support; inject <c>IConfiguration</c> instead).
/// </summary>
/// <remarks>
/// A loader with an unresolvable parameter or wrong return type is skipped with a log entry; an exception thrown by a loader
/// is logged and does not stop startup. Several loaders per module are allowed (reflection order).
/// </remarks>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false, Inherited = false)]
public sealed class AltruistModuleLoaderAttribute : Attribute
{
}
