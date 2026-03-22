/*
Copyright 2025 Aron Gere
Licensed under the Apache License, Version 2.0
*/

using System.Reflection;

using Altruist.UORM;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Altruist.Gaming.Autosave;

/// <summary>
/// Service factory that creates AutosaveService&lt;T&gt; instances for any VaultModel
/// marked with [Autosave]. Integrates with the framework's DI system.
/// </summary>
public sealed class AutosaveServiceFactory : IServiceFactory
{
    public bool CanCreate(Type serviceType)
    {
        if (!serviceType.IsGenericType)
            return false;

        if (serviceType.GetGenericTypeDefinition() != typeof(IAutosaveService<>))
            return false;

        var modelType = serviceType.GetGenericArguments()[0];

        return typeof(IVaultModel).IsAssignableFrom(modelType) &&
               modelType.GetCustomAttribute<AutosaveAttribute>() != null;
    }

    public object Create(IServiceProvider sp, Type serviceType)
    {
        var modelType = serviceType.GetGenericArguments()[0];
        var autosaveAttr = modelType.GetCustomAttribute<AutosaveAttribute>()!;

        var cache = sp.GetRequiredService<ICacheProvider>();
        var coordinator = sp.GetRequiredService<IAutosaveCoordinator>();
        var loggerFactory = sp.GetRequiredService<ILoggerFactory>();

        // Try to resolve IVault<T> — may be null if no DB configured
        var vaultType = typeof(IVault<>).MakeGenericType(modelType);
        var vault = sp.GetService(vaultType);

        var implType = typeof(AutosaveService<>).MakeGenericType(modelType);
        return Activator.CreateInstance(implType, cache, coordinator, loggerFactory, vault, autosaveAttr.BatchSize)!;
    }
}
