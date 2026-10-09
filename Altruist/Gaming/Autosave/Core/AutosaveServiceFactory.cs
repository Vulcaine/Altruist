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
/// <remarks>
/// Picked up automatically as an <see cref="IServiceFactory"/>; you never call it directly. Config keys read when a
/// service is created: <c>altruist:game:autosave:default-batch-size</c>, <c>altruist:game:autosave:wal:enabled</c>
/// (default true; ANDed with <see cref="AutosaveAttribute.Wal"/>), <c>altruist:game:autosave:wal:directory</c>
/// (default <c>data/wal</c>), <c>altruist:game:autosave:wal:flush-interval-seconds</c> (default 10).
/// <c>altruist:game:autosave:default-interval</c> is only read by <see cref="ResolveInterval"/>.
/// <see cref="IVault{TVaultModel}"/> is resolved optionally, so autosave also works cache-only.
/// </remarks>
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

    /// <summary>True for closed <c>IAutosaveService&lt;T&gt;</c> where <c>T</c> is an <see cref="IVaultModel"/> marked
    /// with <see cref="AutosaveAttribute"/>.</summary>
    /// <param name="serviceType">Requested service type.</param>
    /// <returns>Whether <see cref="Create"/> can build it.</returns>
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

    /// <summary>Builds an <see cref="AutosaveService{T}"/> for the model, resolving cache, coordinator, optional vault
    /// and the config values listed on the class.</summary>
    /// <param name="sp">Service provider.</param>
    /// <param name="serviceType">Closed <c>IAutosaveService&lt;T&gt;</c> accepted by <see cref="CanCreate"/>.</param>
    /// <returns>The new service (already registered with the coordinator).</returns>
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
    /// <remarks>Order: attribute cron, then empty string when the attribute has a time-based interval (use
    /// <see cref="AutosaveAttribute.GetTimeSpan"/> instead), then <see cref="DefaultIntervalConfigKey"/>, then
    /// <see cref="FallbackInterval"/>. Use it when building your own flush scheduler.</remarks>
    /// <param name="attr">The model's autosave attribute.</param>
    /// <param name="config">App configuration, or null.</param>
    /// <returns>A cron expression, or <c>""</c> for time-based intervals.</returns>
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
    /// <remarks>An attribute value other than 100 wins; otherwise a positive <see cref="DefaultBatchSizeConfigKey"/>
    /// value, else 100. (An attribute explicitly set to 100 therefore still yields to config.)</remarks>
    /// <param name="attr">The model's autosave attribute.</param>
    /// <param name="config">App configuration, or null.</param>
    /// <returns>Batch size for vault writes.</returns>
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

    /// <summary>Reads <c>altruist:game:autosave:wal:enabled</c>; true when missing or unparsable.</summary>
    /// <param name="config">App configuration, or null.</param>
    /// <returns>Whether the WAL is globally enabled.</returns>
    public static bool ResolveWalEnabled(IConfiguration? config)
    {
        var configValue = config?.GetSection("altruist:game:autosave:wal:enabled")?.Value;
        if (!string.IsNullOrEmpty(configValue) && bool.TryParse(configValue, out var enabled))
            return enabled;
        return true; // Default: WAL enabled
    }
}
