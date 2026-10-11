/* 
Copyright 2025 Aron Gere
Licensed under the Apache License, Version 2.0
*/

namespace Altruist
{
    /// <summary>
    /// Marks an initialization method that the framework calls once on each service instance after the root
    /// service provider has been built. Parameters are resolved like constructor parameters (DI services,
    /// <see cref="AppConfigValueAttribute"/>, <see cref="ServiceKeyAttribute"/>, collections).
    /// </summary>
    /// <remarks>
    /// <para>Rules (enforced by <see cref="DependencyResolver.InvokePostConstructAsync"/>): at most ONE
    /// <c>[PostConstruct]</c> method per type (including inherited ones), it must be a public instance method, and it
    /// must return <c>void</c>, <see cref="System.Threading.Tasks.Task"/> or <see cref="System.Threading.Tasks.ValueTask"/>
    /// (async methods are awaited before startup continues). Violations throw at startup.</para>
    /// <para>Timing: <see cref="AltruistDI.RunPostConstructsAsync"/> resolves every registered (unkeyed) service type from the
    /// root provider at startup (which also makes singletons eager) and invokes the hook once per implementation type.
    /// Instances created later (transient or scoped services resolved after startup) do NOT get the hook called; do that
    /// work in the constructor instead. Use this for initialization that needs the fully built container, async I/O,
    /// or must not run in the constructor.</para>
    /// <para><see cref="Order"/> is currently not used, since only one method per type is allowed.</para>
    /// </remarks>
    /// <example>
    /// <code>
    /// [Service]
    /// public sealed class Warmup
    /// {
    ///     [PostConstruct]
    ///     public async Task InitAsync(IRepository repo, [AppConfigValue("myapp:warmup:count", "10")] int count)
    ///         =&gt; await repo.PreloadAsync(count);
    /// }
    /// </code>
    /// </example>
    [AttributeUsage(AttributeTargets.Method, Inherited = true, AllowMultiple = false)]
    public sealed class PostConstructAttribute : Attribute
    {
        /// <summary>Ordering hint; currently unused (only one <c>[PostConstruct]</c> method per type is allowed).</summary>
        public int Order { get; }

        /// <summary>Marks the method as the type's post-construct hook.</summary>
        /// <param name="order">Ordering hint (currently unused).</param>
        public PostConstructAttribute(int order = 0) => Order = order;
    }
}
