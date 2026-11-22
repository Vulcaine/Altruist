/*
Copyright 2025 Aron Gere
Licensed under the Apache License, Version 2.0
*/

namespace Altruist
{
    /// <summary>
    /// Generic service factory hook used by the dependency planner.
    ///
    /// Provider packages (Postgres, Scylla, Redis, etc.) can implement this to
    /// create specific services (e.g. IVault&lt;T&gt;) during the service
    /// registration / planning phase, without hard-wiring provider logic into core.
    /// </summary>
    /// <remarks>
    /// Implementations need no attribute: every non-abstract <see cref="IServiceFactory"/> class in the loaded assemblies
    /// (that passes <see cref="ConditionalOnConfigAttribute"/>) is registered as a singleton the first time
    /// <see cref="DependencyPlanner"/> meets a constructor dependency that has no <see cref="ServiceAttribute"/> implementation.
    /// The produced service is registered as a singleton and created by the first factory whose <see cref="CanCreate"/> is true.
    /// Use it for families of closed generic services; for a single type use a <see cref="BeanAttribute"/> method instead.
    /// </remarks>
    /// <example>
    /// <code>
    /// public sealed class RepoFactory : IServiceFactory
    /// {
    ///     public bool CanCreate(Type t) =&gt; t.IsGenericType &amp;&amp; t.GetGenericTypeDefinition() == typeof(IRepo&lt;&gt;);
    ///     public object Create(IServiceProvider sp, Type t) =&gt;
    ///         Activator.CreateInstance(typeof(Repo&lt;&gt;).MakeGenericType(t.GetGenericArguments()))!;
    /// }
    /// </code>
    /// </example>
    public interface IServiceFactory
    {
        /// <summary>
        /// Return true if this factory can create the given closed service type
        /// (e.g., IVault&lt;UserProfile&gt;).
        /// </summary>
        /// <param name="serviceType">Closed service type requested by a constructor.</param>
        bool CanCreate(Type serviceType);

        /// <summary>
        /// Create an instance for the given closed service type.
        /// Must return an object that implements that type.
        /// </summary>
        /// <param name="serviceProvider">Provider for resolving the instance's own dependencies.</param>
        /// <param name="serviceType">Closed service type to create.</param>
        object Create(IServiceProvider serviceProvider, Type serviceType);
    }
}
