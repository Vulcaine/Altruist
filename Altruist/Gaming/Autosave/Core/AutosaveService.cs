/*
Copyright 2025 Aron Gere
Licensed under the Apache License, Version 2.0
*/

using System.Collections.Concurrent;

using Microsoft.Extensions.Logging;

namespace Altruist.Gaming.Autosave;

/// <summary>
/// Generic autosave service. Tracks dirty entities by owner, saves to cache immediately,
/// batch-flushes to vault (DB) on interval, and optionally writes to a WAL for crash recovery.
/// </summary>
/// <remarks>
/// Normally created by <see cref="AutosaveServiceFactory"/> when <see cref="IAutosaveService{T}"/> is injected; construct
/// it manually only in tests or for models without <see cref="AutosaveAttribute"/>. The "on interval" part is driven by
/// the caller: nothing inside this class schedules <see cref="FlushAsync"/>. Call <see cref="RecoverFromWalAsync"/> once
/// at startup (it is not on the interface; resolve the concrete type or cast) to replay unflushed WAL entries.
/// </remarks>
/// <typeparam name="T">Vault model type.</typeparam>
public class AutosaveService<T> : IAutosaveService<T>, IDisposable where T : class, IVaultModel
{
    private readonly ConcurrentDictionary<string, string> _dirtyMap = new(); // storageId → ownerId
    private readonly ICacheProvider _cache;
    private readonly IVault<T>? _vault;
    private readonly WriteAheadLog<T>? _wal;
    private readonly ILogger _logger;
    private readonly int _batchSize;

    /// <inheritdoc/>
    public int DirtyCount => _dirtyMap.Count;

    /// <summary>Creates the service and registers it with <paramref name="coordinator"/>.</summary>
    /// <param name="cache">Cache that holds the latest entity snapshots (written on every <see cref="MarkDirty(T, string)"/>).</param>
    /// <param name="coordinator">Coordinator this service registers itself with.</param>
    /// <param name="loggerFactory">Logger factory.</param>
    /// <param name="vault">Database vault; when null, flushes only clear dirty flags and nothing reaches a database.</param>
    /// <param name="batchSize">Entities per <c>SaveBatchAsync</c> call during flush.</param>
    /// <param name="walEnabled">Create a <see cref="WriteAheadLog{T}"/> for crash recovery.</param>
    /// <param name="walDirectory">Directory of the WAL file (created if missing; relative to the working directory).</param>
    /// <param name="walFlushIntervalSeconds">How often the WAL buffer is appended to disk, in seconds.</param>
    public AutosaveService(
        ICacheProvider cache,
        IAutosaveCoordinator coordinator,
        ILoggerFactory loggerFactory,
        IVault<T>? vault = null,
        int batchSize = 100,
        bool walEnabled = true,
        string walDirectory = "data/wal",
        int walFlushIntervalSeconds = 10)
    {
        _cache = cache;
        _vault = vault;
        _batchSize = batchSize;
        _logger = loggerFactory.CreateLogger($"Autosave<{typeof(T).Name}>");

        if (walEnabled)
            _wal = new WriteAheadLog<T>(walDirectory, walFlushIntervalSeconds, loggerFactory);

        coordinator.Register(this);
    }

    /// <inheritdoc/>
    public void MarkDirty(T entity)
    {
        EnsureStorageId(entity);
        MarkDirty(entity, entity.StorageId);
    }

    /// <inheritdoc/>
    public void MarkDirty(T entity, string ownerId)
    {
        EnsureStorageId(entity);
        _dirtyMap[entity.StorageId] = ownerId;
        _ = _cache.SaveAsync(entity.StorageId, entity);
        _wal?.Append(entity, ownerId);
    }

    /// <inheritdoc/>
    public async Task SaveAsync(T entity)
    {
        EnsureStorageId(entity);
        await _cache.SaveAsync(entity.StorageId, entity);

        if (_vault != null)
            await _vault.SaveAsync(entity);

        _dirtyMap.TryRemove(entity.StorageId, out _);
    }

    // Fresh vault rows arrive here with an empty StorageId — the vault layer's
    // OnSave hook would assign one inside _vault.SaveAsync, but by then the
    // cache key (and dirty-map key) above are already empty-string, so every
    // row in a save batch would silently overwrite the same cache slot. Run
    // the same id-assignment OnSave does, but only for the StorageId — leave
    // Timestamp/Type/Version for the real OnSave inside the vault save.
    private static void EnsureStorageId(T entity)
    {
        if (!string.IsNullOrEmpty(entity.StorageId)) return;
        entity.StorageId = entity is IIdGenerator generator
            ? generator.GenerateId()
            : Guid.NewGuid().ToString();
    }

    /// <inheritdoc/>
    public async Task<T?> LoadAsync(string storageId)
    {
        var cached = await _cache.GetAsync<T>(storageId);
        if (cached != null) return cached;

        if (_vault == null) return null;

        var fromDb = await _vault.Where(e => e.StorageId == storageId).FirstOrDefaultAsync();
        if (fromDb != null)
            await _cache.SaveAsync(fromDb.StorageId, fromDb);

        return fromDb;
    }

    /// <inheritdoc/>
    public async Task FlushByOwnerAsync(string ownerId)
    {
        var ids = _dirtyMap
            .Where(kv => string.Equals(kv.Value, ownerId, StringComparison.Ordinal))
            .Select(kv => kv.Key)
            .ToList();

        if (ids.Count > 0)
            await FlushIdsAsync(ids);
    }

    /// <inheritdoc/>
    public async Task FlushAsync()
    {
        var ids = _dirtyMap.Keys.ToList();
        if (ids.Count > 0)
            await FlushIdsAsync(ids);

        // Truncate WAL after successful full flush — data is now safe in DB
        if (_wal != null)
            await _wal.TruncateAsync();
    }

    /// <summary>
    /// Recover unflushed data from WAL file (called on startup).
    /// Returns the number of recovered entities.
    /// </summary>
    /// <remarks>Deserializes the latest WAL snapshot per storage id, batch-saves them to the vault (falling back to
    /// per-entity saves), then truncates the WAL. Without a vault the entries are discarded. Call before gameplay
    /// starts mutating entities of this type.</remarks>
    /// <returns>Number of distinct entities found in the WAL (including any that failed to deserialize or save).</returns>
    public async Task<int> RecoverFromWalAsync()
    {
        if (_wal == null) return 0;

        var entries = await _wal.RecoverAsync();
        if (entries.Count == 0) return 0;

        _logger.LogInformation("Recovering {Count} {Type} entities from WAL...", entries.Count, typeof(T).Name);

        if (_vault != null)
        {
            var entities = new List<T>();
            foreach (var entry in entries)
            {
                try
                {
                    var entity = System.Text.Json.JsonSerializer.Deserialize<T>(entry.Data);
                    if (entity != null) entities.Add(entity);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to deserialize WAL entry {Id}", entry.StorageId);
                }
            }

            if (entities.Count > 0)
            {
                try
                {
                    await _vault.SaveBatchAsync(entities);
                    _logger.LogInformation("WAL recovery: saved {Count} {Type} entities to DB", entities.Count, typeof(T).Name);
                }
                catch
                {
                    foreach (var entity in entities)
                    {
                        try { await _vault.SaveAsync(entity); }
                        catch (Exception ex) { _logger.LogError(ex, "WAL recovery failed for {Id}", entity.StorageId); }
                    }
                }
            }
        }

        await _wal.TruncateAsync();
        return entries.Count;
    }

    private async Task FlushIdsAsync(List<string> ids)
    {
        if (_vault == null)
        {
            foreach (var id in ids) _dirtyMap.TryRemove(id, out _);
            return;
        }

        foreach (var batch in ids.Chunk(_batchSize))
        {
            var entities = new List<T>();
            foreach (var id in batch)
            {
                var entity = await _cache.GetAsync<T>(id);
                if (entity != null) entities.Add(entity);
            }

            if (entities.Count == 0) continue;

            try
            {
                await _vault.SaveBatchAsync(entities);
                foreach (var id in batch) _dirtyMap.TryRemove(id, out _);
                _logger.LogDebug("Flushed {Count} {Type} entities", entities.Count, typeof(T).Name);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Batch save failed for {Type}, falling back to individual saves", typeof(T).Name);

                foreach (var entity in entities)
                {
                    try
                    {
                        await _vault.SaveAsync(entity);
                        _dirtyMap.TryRemove(entity.StorageId, out _);
                    }
                    catch (Exception innerEx)
                    {
                        _logger.LogError(innerEx, "Failed to save {Type} {Id}, will retry next flush",
                            typeof(T).Name, entity.StorageId);
                    }
                }
            }
        }
    }

    /// <summary>Stops the WAL timer. Does not flush: call <see cref="FlushAsync"/> first on shutdown.</summary>
    public void Dispose()
    {
        _wal?.Dispose();
    }
}
