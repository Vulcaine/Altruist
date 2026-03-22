/*
Copyright 2025 Aron Gere
Licensed under the Apache License, Version 2.0
*/

using System.Collections.Concurrent;

using Microsoft.Extensions.Logging;

namespace Altruist.Gaming.Autosave;

/// <summary>
/// Generic autosave service. Tracks dirty entities by owner, saves to cache immediately,
/// and batch-flushes to vault (DB) on interval or on demand.
/// </summary>
public class AutosaveService<T> : IAutosaveService<T> where T : class, IVaultModel
{
    private readonly ConcurrentDictionary<string, string> _dirtyMap = new(); // storageId → ownerId
    private readonly ICacheProvider _cache;
    private readonly IVault<T>? _vault;
    private readonly ILogger _logger;
    private readonly int _batchSize;

    public int DirtyCount => _dirtyMap.Count;

    public AutosaveService(
        ICacheProvider cache,
        IAutosaveCoordinator coordinator,
        ILoggerFactory loggerFactory,
        IVault<T>? vault = null,
        int batchSize = 100)
    {
        _cache = cache;
        _vault = vault;
        _batchSize = batchSize;
        _logger = loggerFactory.CreateLogger($"Autosave<{typeof(T).Name}>");

        coordinator.Register(this);
    }

    public void MarkDirty(T entity, string ownerId)
    {
        _dirtyMap[entity.StorageId] = ownerId;
        _ = _cache.SaveAsync(entity.StorageId, entity);
    }

    public async Task SaveAsync(T entity)
    {
        await _cache.SaveAsync(entity.StorageId, entity);

        if (_vault != null)
            await _vault.SaveAsync(entity);

        _dirtyMap.TryRemove(entity.StorageId, out _);
    }

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

    public async Task FlushByOwnerAsync(string ownerId)
    {
        var ids = _dirtyMap
            .Where(kv => string.Equals(kv.Value, ownerId, StringComparison.Ordinal))
            .Select(kv => kv.Key)
            .ToList();

        if (ids.Count > 0)
            await FlushIdsAsync(ids);
    }

    public async Task FlushAsync()
    {
        var ids = _dirtyMap.Keys.ToList();
        if (ids.Count > 0)
            await FlushIdsAsync(ids);
    }

    private async Task FlushIdsAsync(List<string> ids)
    {
        if (_vault == null)
        {
            // No vault configured — just clear dirty flags (cache-only mode)
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
}
