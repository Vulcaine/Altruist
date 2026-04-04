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
    public interface IServiceFactory
    {
        /// <summary>
        /// Return true if this factory can create the given closed service type
        /// (e.g., IVault&lt;UserProfile&gt;).
        /// </summary>
        bool CanCreate(Type serviceType);

        /// <summary>
        /// Create an instance for the given closed service type.
        /// Must return an object that implements that type.
        /// </summary>
        object Create(IServiceProvider serviceProvider, Type serviceType);
    }
}
