/*
Copyright 2025 Aron Gere
Licensed under the Apache License, Version 2.0
*/

using System.Collections.Concurrent;
using Altruist;
using Altruist.Gaming.Autosave;
using Altruist.InMemory;
using Microsoft.Extensions.Logging.Abstractions;

namespace Tests.Gaming.Autosave;

[Autosave("*/5 * * * *")]
public class TestPlayerVault : VaultModel
{
    public override string StorageId { get; set; } = Guid.NewGuid().ToString();
    public string Name { get; set; } = "";
    public int Level { get; set; } = 1;
    public int Gold { get; set; }
}

public class AutosaveServiceTests
{
    private AutosaveService<TestPlayerVault> CreateService(out AutosaveCoordinator coordinator)
    {
        var cache = new InMemoryCache();
        coordinator = new AutosaveCoordinator(NullLoggerFactory.Instance);
        return new AutosaveService<TestPlayerVault>(
            cache, coordinator, NullLoggerFactory.Instance, vault: null, batchSize: 100);
    }

    [Fact]
    public void MarkDirty_ShouldIncrementDirtyCount()
    {
        var service = CreateService(out _);
        var player = new TestPlayerVault { StorageId = "p1", Name = "Alice" };

        service.MarkDirty(player, "owner1");

        Assert.Equal(1, service.DirtyCount);
    }

    [Fact]
    public void MarkDirty_SameEntity_ShouldNotDuplicate()
    {
        var service = CreateService(out _);
        var player = new TestPlayerVault { StorageId = "p1" };

        service.MarkDirty(player, "owner1");
        service.MarkDirty(player, "owner1");

        Assert.Equal(1, service.DirtyCount);
    }

    [Fact]
    public async Task FlushAsync_ShouldClearDirtyFlags_InCacheOnlyMode()
    {
        var service = CreateService(out _);
        var player = new TestPlayerVault { StorageId = "p1" };

        service.MarkDirty(player, "owner1");
        Assert.Equal(1, service.DirtyCount);

        await service.FlushAsync();
        Assert.Equal(0, service.DirtyCount);
    }

    [Fact]
    public async Task FlushByOwnerAsync_ShouldOnlyFlushOwnerEntities()
    {
        var service = CreateService(out _);
        var p1 = new TestPlayerVault { StorageId = "p1" };
        var p2 = new TestPlayerVault { StorageId = "p2" };

        service.MarkDirty(p1, "owner1");
        service.MarkDirty(p2, "owner2");
        Assert.Equal(2, service.DirtyCount);

        await service.FlushByOwnerAsync("owner1");

        Assert.Equal(1, service.DirtyCount); // only owner2 remains
    }

    [Fact]
    public async Task LoadAsync_ShouldReturnCachedEntity()
    {
        var service = CreateService(out _);
        var player = new TestPlayerVault { StorageId = "p1", Name = "Bob", Gold = 500 };

        service.MarkDirty(player, "owner1"); // This saves to cache

        var loaded = await service.LoadAsync("p1");
        Assert.NotNull(loaded);
        Assert.Equal("Bob", loaded.Name);
        Assert.Equal(500, loaded.Gold);
    }

    [Fact]
    public async Task SaveAsync_ShouldRemoveFromDirtyMap()
    {
        var service = CreateService(out _);
        var player = new TestPlayerVault { StorageId = "p1" };

        service.MarkDirty(player, "owner1");
        Assert.Equal(1, service.DirtyCount);

        await service.SaveAsync(player);
        Assert.Equal(0, service.DirtyCount);
    }
}

public class AutosaveCoordinatorTests
{
    [Fact]
    public async Task FlushByOwnerAsync_ShouldFlushAcrossAllServices()
    {
        var cache = new InMemoryCache();
        var coordinator = new AutosaveCoordinator(NullLoggerFactory.Instance);

        var service1 = new AutosaveService<TestPlayerVault>(
            cache, coordinator, NullLoggerFactory.Instance, vault: null);

        var p1 = new TestPlayerVault { StorageId = "p1" };
        service1.MarkDirty(p1, "player123");

        Assert.Equal(1, coordinator.TotalDirtyCount);

        await coordinator.FlushByOwnerAsync("player123");

        Assert.Equal(0, coordinator.TotalDirtyCount);
    }

    [Fact]
    public async Task FlushAllAsync_ShouldFlushEverything()
    {
        var cache = new InMemoryCache();
        var coordinator = new AutosaveCoordinator(NullLoggerFactory.Instance);

        var service = new AutosaveService<TestPlayerVault>(
            cache, coordinator, NullLoggerFactory.Instance, vault: null);

        service.MarkDirty(new TestPlayerVault { StorageId = "p1" }, "owner1");
        service.MarkDirty(new TestPlayerVault { StorageId = "p2" }, "owner2");
        service.MarkDirty(new TestPlayerVault { StorageId = "p3" }, "owner3");

        Assert.Equal(3, coordinator.TotalDirtyCount);

        await coordinator.FlushAllAsync();

        Assert.Equal(0, coordinator.TotalDirtyCount);
    }

    [Fact]
    public void TotalDirtyCount_ShouldSumAcrossServices()
    {
        var cache = new InMemoryCache();
        var coordinator = new AutosaveCoordinator(NullLoggerFactory.Instance);

        var s1 = new AutosaveService<TestPlayerVault>(
            cache, coordinator, NullLoggerFactory.Instance, vault: null);

        s1.MarkDirty(new TestPlayerVault { StorageId = "a" }, "o1");
        s1.MarkDirty(new TestPlayerVault { StorageId = "b" }, "o2");

        Assert.Equal(2, coordinator.TotalDirtyCount);
    }
}
