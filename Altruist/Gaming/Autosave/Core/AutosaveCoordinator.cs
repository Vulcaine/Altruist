/*
Copyright 2025 Aron Gere
Licensed under the Apache License, Version 2.0
*/

using Microsoft.Extensions.Logging;

namespace Altruist.Gaming.Autosave;

/// <summary>
/// Singleton coordinator that tracks all IAutosaveServiceBase instances.
/// Provides owner-level and global flush operations.
/// </summary>
[Service(typeof(IAutosaveCoordinator))]
public class AutosaveCoordinator : IAutosaveCoordinator
{
    private readonly List<IAutosaveServiceBase> _services = new();
    private readonly object _lock = new();
    private readonly ILogger _logger;

    public AutosaveCoordinator(ILoggerFactory loggerFactory)
    {
        _logger = loggerFactory.CreateLogger<AutosaveCoordinator>();
    }

    public int TotalDirtyCount
    {
        get
        {
            lock (_lock)
                return _services.Sum(s => s.DirtyCount);
        }
    }

    public void Register(IAutosaveServiceBase service)
    {
        lock (_lock)
        {
            _services.Add(service);
            _logger.LogInformation("Registered autosave service: {Type}", service.GetType().Name);
        }
    }

    public async Task FlushByOwnerAsync(string ownerId)
    {
        List<IAutosaveServiceBase> snapshot;
        lock (_lock)
            snapshot = _services.ToList();

        foreach (var service in snapshot)
        {
            try
            {
                await service.FlushByOwnerAsync(ownerId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to flush {Service} for owner {Owner}",
                    service.GetType().Name, ownerId);
            }
        }
    }

    public async Task FlushAllAsync()
    {
        List<IAutosaveServiceBase> snapshot;
        lock (_lock)
            snapshot = _services.ToList();

        _logger.LogInformation("Flushing all autosave services ({Count} services, {Dirty} dirty entities)",
            snapshot.Count, TotalDirtyCount);

        foreach (var service in snapshot)
        {
            try
            {
                await service.FlushAsync();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to flush {Service}", service.GetType().Name);
            }
        }
    }
}
