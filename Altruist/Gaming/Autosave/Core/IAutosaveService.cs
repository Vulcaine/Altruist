/*
Copyright 2025 Aron Gere
Licensed under the Apache License, Version 2.0
*/

namespace Altruist.Gaming.Autosave;

/// <summary>Unit of <see cref="AutosaveAttribute.IntervalValue"/> for time-based autosave intervals.</summary>
public enum AutosaveCycle
{
    /// <summary>Interval in seconds.</summary>
    Seconds,
    /// <summary>Interval in minutes.</summary>
    Minutes,
    /// <summary>Interval in hours.</summary>
    Hours
}

/// <summary>
/// Attribute to mark a VaultModel for automatic dirty-tracking and periodic DB flush.
/// Supports both time-based and cron-based intervals.
/// </summary>
/// <remarks>
/// <para>
/// Effect: makes <see cref="IAutosaveService{T}"/> injectable for the model (created by
/// <see cref="AutosaveServiceFactory"/>; without the attribute the factory refuses the type). The interval is
/// metadata only: the framework does not schedule flushes by itself. Read it via
/// <see cref="AutosaveServiceFactory.ResolveInterval"/> / <see cref="GetTimeSpan"/> and call
/// <see cref="IAutosaveServiceBase.FlushAsync"/> (or <see cref="IAutosaveCoordinator.FlushAllAsync"/>) from your own
/// timer or scheduled job, and flush per owner on disconnect.
/// </para>
/// <para>
/// Choosing: use autosave for frequently mutated state (player stats, inventory) where writing to the database on
/// every change is too expensive. For rarely changed or must-be-durable-now data, call the <see cref="IVault{TVaultModel}"/>
/// directly (or <see cref="IAutosaveService{T}.SaveAsync"/>).
/// </para>
/// </remarks>
/// <example>
/// <code>
/// [Vault("player")]
/// [Autosave(30, AutosaveCycle.Seconds, BatchSize = 200)]
/// public class PlayerVault : VaultModel { ... }
///
/// [Autosave("*/5 * * * *", Wal = false)]   // cron, no write-ahead log
/// public class StatsVault : VaultModel { ... }
/// </code>
/// </example>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public class AutosaveAttribute : Attribute
{
    /// <summary>Cron expression for flush interval (used when no numeric interval set).</summary>
    public string CronExpression { get; }

    /// <summary>Numeric interval value.</summary>
    public int IntervalValue { get; }

    /// <summary>Unit for the numeric interval.</summary>
    public AutosaveCycle Unit { get; }

    /// <summary>
    /// Batch size for DB writes. Default: 100. Overridable via config
    /// (altruist:game:autosave:default-batch-size) or per-attribute.
    /// </summary>
    public int BatchSize { get; set; } = 100;

    /// <summary>
    /// Enable write-ahead log for crash recovery. Default: true.
    /// Set to false for high-frequency low-value data where losing a few seconds is acceptable.
    /// </summary>
    public bool Wal { get; set; } = true;

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
    /// Default: uses interval from config (altruist:game:autosave:default-interval).
    /// Falls back to every 5 minutes if not configured.
    /// </summary>
    public AutosaveAttribute()
    {
        CronExpression = "";
        IntervalValue = 0;
        Unit = AutosaveCycle.Seconds;
    }

    /// <summary>Whether this attribute uses the global default interval from config.</summary>
    public bool UsesDefaultInterval => IntervalValue <= 0 && string.IsNullOrEmpty(CronExpression);

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
/// <remarks>Every <see cref="AutosaveService{T}"/> registers itself with <see cref="IAutosaveCoordinator"/> on
/// construction; use the coordinator to flush across all model types instead of holding each service.</remarks>
public interface IAutosaveServiceBase
{
    /// <summary>
    /// Writes every dirty entity of this type from cache to the vault in batches, then truncates the write-ahead log
    /// when nothing is left dirty. Call it from your periodic autosave job and on shutdown. Entities whose save fails
    /// stay dirty (and in the write-ahead log) and are retried on the next flush. Without a vault it just clears the
    /// dirty set.
    /// </summary>
    /// <returns>A task completing when the flush has finished.</returns>
    Task FlushAsync();
    /// <summary>
    /// Writes only the dirty entities marked with <paramref name="ownerId"/> (e.g. on player disconnect). Does not
    /// truncate the write-ahead log.
    /// </summary>
    /// <param name="ownerId">Owner id passed to <see cref="IAutosaveService{T}.MarkDirty(T, string)"/> (ordinal match).</param>
    /// <returns>A task completing when the flush has finished.</returns>
    Task FlushByOwnerAsync(string ownerId);
    /// <summary>Number of entities currently marked dirty and not yet flushed.</summary>
    int DirtyCount { get; }
}

/// <summary>
/// Generic autosave service for a specific VaultModel type.
/// Provides dirty tracking, batched DB flush, and cache-first reads.
/// </summary>
/// <remarks>
/// Inject <c>IAutosaveService&lt;TModel&gt;</c> for any vault model annotated with <see cref="AutosaveAttribute"/>; one
/// singleton per model type is created by <see cref="AutosaveServiceFactory"/>. Flow: mutate the entity, call
/// <see cref="MarkDirty(T, string)"/> (writes to <see cref="ICacheProvider"/> right away and, if enabled, to the
/// write-ahead log), then let your periodic job or a disconnect call <see cref="IAutosaveServiceBase.FlushAsync"/> /
/// <see cref="IAutosaveServiceBase.FlushByOwnerAsync"/> to persist to the vault. Read back with
/// <see cref="LoadAsync"/>, which sees unflushed changes because it reads the cache first.
/// Safe to call from multiple threads.
/// </remarks>
/// <typeparam name="T">Vault model type (must carry <see cref="AutosaveAttribute"/> to be resolvable).</typeparam>
/// <example>
/// <code>
/// public sealed class LevelUpService(IAutosaveService&lt;PlayerVault&gt; players)
/// {
///     public void LevelUp(PlayerVault p, string accountId) { p.Level++; players.MarkDirty(p, accountId); }
/// }
/// // on disconnect:  await coordinator.FlushByOwnerAsync(accountId);
/// </code>
/// </example>
public interface IAutosaveService<T> : IAutosaveServiceBase where T : class, IVaultModel
{
    /// <summary>
    /// Mark an entity as dirty using its own storage ID as the owner ID.
    /// Useful for root entities where owner identity and storage identity are the same.
    /// </summary>
    /// <param name="entity">The entity that changed.</param>
    /// <remarks>Assigns a <c>StorageId</c> first if the entity has none.</remarks>
    void MarkDirty(T entity);

    /// <summary>
    /// Mark an entity as dirty. It will be saved to cache immediately
    /// and flushed to DB on the next interval or on disconnect.
    /// </summary>
    /// <param name="entity">The entity that changed.</param>
    /// <param name="ownerId">Owner ID (player/guild) for disconnect-save grouping.</param>
    /// <remarks>Assigns a <c>StorageId</c> first if the entity has none (via <see cref="IIdGenerator"/> or a new GUID).
    /// The cache write is fire-and-forget; marking the same entity again just updates its owner and snapshot.</remarks>
    void MarkDirty(T entity, string ownerId);

    /// <summary>Force-save a single entity to DB immediately.</summary>
    /// <remarks>Writes cache, then vault (skipped when no vault is configured), and clears its dirty flag. Use for
    /// changes that must not wait for the next flush (purchases, trades).</remarks>
    /// <param name="entity">Entity to persist.</param>
    /// <returns>A task completing after both writes.</returns>
    Task SaveAsync(T entity);

    /// <summary>Load an entity from cache (or DB fallback) by its storage ID.</summary>
    /// <remarks>A DB hit is written back to the cache.</remarks>
    /// <param name="storageId">The entity's storage id.</param>
    /// <returns>The entity, or null when found in neither cache nor vault.</returns>
    Task<T?> LoadAsync(string storageId);
}

/// <summary>
/// Singleton coordinator that knows about all autosave services.
/// Used for disconnect-save and shutdown-save across all entity types.
/// </summary>
/// <remarks>
/// Registered as a singleton (<see cref="AutosaveCoordinator"/>). Note that only autosave services that have been
/// resolved at least once are registered, since each service registers itself in its constructor. Flush failures of
/// one service are logged and do not stop the others.
/// </remarks>
public interface IAutosaveCoordinator
{
    /// <summary>Adds a service to the flush set. Called by <see cref="AutosaveService{T}"/>'s constructor; you rarely
    /// call it yourself (only for custom <see cref="IAutosaveServiceBase"/> implementations).</summary>
    /// <param name="service">Service to track (not de-duplicated).</param>
    void Register(IAutosaveServiceBase service);

    /// <summary>Flush all dirty data for a specific owner across ALL entity types.</summary>
    Task FlushByOwnerAsync(string ownerId);

    /// <summary>Flush everything across all entity types (server shutdown).</summary>
    Task FlushAllAsync();

    /// <summary>Sum of <see cref="IAutosaveServiceBase.DirtyCount"/> over all registered services.</summary>
    int TotalDirtyCount { get; }
}
