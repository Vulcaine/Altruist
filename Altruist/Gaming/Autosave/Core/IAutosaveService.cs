/*
Copyright 2025 Aron Gere
Licensed under the Apache License, Version 2.0
*/

namespace Altruist.Gaming.Autosave;

public enum AutosaveCycle
{
    Seconds,
    Minutes,
    Hours
}

/// <summary>
/// Attribute to mark a VaultModel for automatic dirty-tracking and periodic DB flush.
/// Supports both time-based and cron-based intervals.
/// </summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public class AutosaveAttribute : Attribute
{
    /// <summary>Cron expression for flush interval (used when no numeric interval set).</summary>
    public string CronExpression { get; }

    /// <summary>Numeric interval value.</summary>
    public int IntervalValue { get; }

    /// <summary>Unit for the numeric interval.</summary>
    public AutosaveCycle Unit { get; }

    /// <summary>Batch size for DB writes. Default: 100.</summary>
    public int BatchSize { get; set; } = 100;

    /// <summary>
    /// Time-based autosave interval.
    /// Example: [Autosave(120, AutosaveCycle.Seconds)] — flushes every 120 seconds.
    /// </summary>
    public AutosaveAttribute(int interval, AutosaveCycle unit = AutosaveCycle.Seconds)
    {
        IntervalValue = interval;
        Unit = unit;
        CronExpression = "";
    }

    /// <summary>
    /// Cron-based autosave interval.
    /// Example: [Autosave("*/5 * * * *")] — flushes every 5 minutes.
    /// </summary>
    public AutosaveAttribute(string cronExpression)
    {
        CronExpression = cronExpression;
        IntervalValue = 0;
        Unit = AutosaveCycle.Seconds;
    }

    /// <summary>
    /// Default: flushes every 5 minutes.
    /// </summary>
    public AutosaveAttribute()
    {
        CronExpression = "*/5 * * * *";
        IntervalValue = 0;
        Unit = AutosaveCycle.Seconds;
    }

    /// <summary>Get the interval as TimeSpan (for time-based), or null (for cron-based).</summary>
    public TimeSpan? GetTimeSpan()
    {
        if (IntervalValue <= 0) return null;
        return Unit switch
        {
            AutosaveCycle.Seconds => TimeSpan.FromSeconds(IntervalValue),
            AutosaveCycle.Minutes => TimeSpan.FromMinutes(IntervalValue),
            AutosaveCycle.Hours => TimeSpan.FromHours(IntervalValue),
            _ => TimeSpan.FromSeconds(IntervalValue)
        };
    }
}

/// <summary>
/// Non-generic base interface tracked by the coordinator.
/// </summary>
public interface IAutosaveServiceBase
{
    Task FlushAsync();
    Task FlushByOwnerAsync(string ownerId);
    int DirtyCount { get; }
}

/// <summary>
/// Generic autosave service for a specific VaultModel type.
/// Provides dirty tracking, batched DB flush, and cache-first reads.
/// </summary>
public interface IAutosaveService<T> : IAutosaveServiceBase where T : class, IVaultModel
{
    /// <summary>
    /// Mark an entity as dirty. It will be saved to cache immediately
    /// and flushed to DB on the next interval or on disconnect.
    /// </summary>
    /// <param name="entity">The entity that changed.</param>
    /// <param name="ownerId">Owner ID (player/guild) for disconnect-save grouping.</param>
    void MarkDirty(T entity, string ownerId);

    /// <summary>Force-save a single entity to DB immediately.</summary>
    Task SaveAsync(T entity);

    /// <summary>Load an entity from cache (or DB fallback) by its storage ID.</summary>
    Task<T?> LoadAsync(string storageId);
}

/// <summary>
/// Singleton coordinator that knows about all autosave services.
/// Used for disconnect-save and shutdown-save across all entity types.
/// </summary>
public interface IAutosaveCoordinator
{
    void Register(IAutosaveServiceBase service);

    /// <summary>Flush all dirty data for a specific owner across ALL entity types.</summary>
    Task FlushByOwnerAsync(string ownerId);

    /// <summary>Flush everything across all entity types (server shutdown).</summary>
    Task FlushAllAsync();

    int TotalDirtyCount { get; }
}
