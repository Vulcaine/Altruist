/*
Copyright 2025 Aron Gere
Licensed under the Apache License, Version 2.0
*/

using System.Reflection;

using Altruist.UORM;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Altruist.Gaming.Autosave;

/// <summary>
/// Service factory that creates AutosaveService&lt;T&gt; instances for any VaultModel
/// marked with [Autosave]. Integrates with the framework's DI system.
/// </summary>
public sealed class AutosaveServiceFactory : IServiceFactory
{
    /// <summary>Config key for the global default autosave interval.</summary>
    public const string DefaultIntervalConfigKey = "altruist:game:autosave:default-interval";

    /// <summary>Config key for the global default batch size.</summary>
    public const string DefaultBatchSizeConfigKey = "altruist:game:autosave:default-batch-size";

    /// <summary>Fallback when neither attribute nor config specifies an interval.</summary>
    public const string FallbackInterval = "*/5 * * * *";

    /// <summary>Fallback batch size.</summary>
    public const int FallbackBatchSize = 100;

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

        var config = sp.GetService<IConfiguration>();
        var batchSize = ResolveBatchSize(autosaveAttr, config);

        // WAL config
        var walEnabled = autosaveAttr.Wal && ResolveWalEnabled(config);
        var walDirectory = config?.GetSection("altruist:game:autosave:wal:directory")?.Value ?? "data/wal";
        var walFlushStr = config?.GetSection("altruist:game:autosave:wal:flush-interval-seconds")?.Value;
        var walFlushInterval = !string.IsNullOrEmpty(walFlushStr) && int.TryParse(walFlushStr, out var wf) ? wf : 10;

        var implType = typeof(AutosaveService<>).MakeGenericType(modelType);
        return Activator.CreateInstance(implType, cache, coordinator, loggerFactory, vault,
            batchSize, walEnabled, walDirectory, walFlushInterval)!;
    }

    /// <summary>
    /// Resolve the effective cron expression for an [Autosave] attribute,
    /// considering config defaults.
    /// </summary>
    public static string ResolveInterval(AutosaveAttribute attr, IConfiguration? config)
    {
        // Explicit cron expression on attribute takes priority
        if (!string.IsNullOrEmpty(attr.CronExpression))
            return attr.CronExpression;

        // Explicit time-based interval on attribute — convert to cron isn't needed,
        // callers should use attr.GetTimeSpan() for these
        if (attr.IntervalValue > 0)
            return "";

        // Default: read from config
        var configValue = config?.GetSection(DefaultIntervalConfigKey)?.Value;
        if (!string.IsNullOrEmpty(configValue))
            return configValue;

        // Ultimate fallback
        return FallbackInterval;
    }

    /// <summary>
    /// Resolve the effective batch size, considering attribute and config defaults.
    /// </summary>
    public static int ResolveBatchSize(AutosaveAttribute attr, IConfiguration? config)
    {
        // Config can override the default (100)
        var configValue = config?.GetSection(DefaultBatchSizeConfigKey)?.Value;
        var configBatchSize = !string.IsNullOrEmpty(configValue) && int.TryParse(configValue, out var parsed) && parsed > 0
            ? parsed
            : FallbackBatchSize;

        // Attribute value wins if explicitly changed from default
        return attr.BatchSize != FallbackBatchSize ? attr.BatchSize : configBatchSize;
    }

    public static bool ResolveWalEnabled(IConfiguration? config)
    {
        var configValue = config?.GetSection("altruist:game:autosave:wal:enabled")?.Value;
        if (!string.IsNullOrEmpty(configValue) && bool.TryParse(configValue, out var enabled))
            return enabled;
        return true; // Default: WAL enabled
    }
}
