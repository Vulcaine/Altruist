/*
Copyright 2025 Aron Gere
Licensed under the Apache License, Version 2.0
*/

using System.Collections.Concurrent;
using System.Text.Json;
using Altruist;
using Altruist.Gaming.Autosave;
using Altruist.InMemory;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Tests.Gaming.Autosave;

// ───────────────────────── Test Models ─────────────────────────

[Autosave("*/5 * * * *")]
public class TestPlayerVault : VaultModel
{
    public override string StorageId { get; set; } = Guid.NewGuid().ToString();
    public string Name { get; set; } = "";
    public int Level { get; set; } = 1;
    public int Gold { get; set; }
}

[Autosave(30, AutosaveCycle.Seconds, Wal = false)]
public class TestItemVault : VaultModel
{
    public override string StorageId { get; set; } = Guid.NewGuid().ToString();
    public string ItemName { get; set; } = "";
    public int Quantity { get; set; }
}

// ═══════════════════════════════════════════════════════════════
//  UNIT TESTS — AutosaveService (cache-only, no vault)
// ═══════════════════════════════════════════════════════════════

public class AutosaveServiceTests
{
    private AutosaveService<TestPlayerVault> CreateService(
        out AutosaveCoordinator coordinator,
        bool walEnabled = false)
    {
        var cache = new InMemoryCache();
        coordinator = new AutosaveCoordinator(NullLoggerFactory.Instance);
        return new AutosaveService<TestPlayerVault>(
            cache, coordinator, NullLoggerFactory.Instance,
            vault: null, batchSize: 100, walEnabled: walEnabled);
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
    public void MarkDirty_MultipleEntities_ShouldTrackAll()
    {
        var service = CreateService(out _);

        for (int i = 0; i < 50; i++)
            service.MarkDirty(new TestPlayerVault { StorageId = $"p{i}" }, "owner1");

        Assert.Equal(50, service.DirtyCount);
    }

    [Fact]
    public void MarkDirty_SameEntity_DifferentOwner_ShouldUpdateOwner()
    {
        var service = CreateService(out _);
        var player = new TestPlayerVault { StorageId = "p1" };

        service.MarkDirty(player, "owner1");
        service.MarkDirty(player, "owner2");

        // Still only 1 dirty entry (same storageId)
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
    public async Task FlushAsync_EmptyDirtyMap_ShouldBeNoOp()
    {
        var service = CreateService(out _);

        await service.FlushAsync(); // should not throw
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
    public async Task FlushByOwnerAsync_NonExistentOwner_ShouldBeNoOp()
    {
        var service = CreateService(out _);
        service.MarkDirty(new TestPlayerVault { StorageId = "p1" }, "owner1");

        await service.FlushByOwnerAsync("nonexistent");

        Assert.Equal(1, service.DirtyCount);
    }

    [Fact]
    public async Task FlushByOwnerAsync_MultipleEntitiesSameOwner_ShouldFlushAll()
    {
        var service = CreateService(out _);
        service.MarkDirty(new TestPlayerVault { StorageId = "p1" }, "owner1");
        service.MarkDirty(new TestPlayerVault { StorageId = "p2" }, "owner1");
        service.MarkDirty(new TestPlayerVault { StorageId = "p3" }, "owner1");
        service.MarkDirty(new TestPlayerVault { StorageId = "p4" }, "owner2");

        await service.FlushByOwnerAsync("owner1");

        Assert.Equal(1, service.DirtyCount); // only owner2's p4
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
    public async Task LoadAsync_NonExistentEntity_ShouldReturnNull()
    {
        var service = CreateService(out _);

        var loaded = await service.LoadAsync("doesnotexist");
        Assert.Null(loaded);
    }

    [Fact]
    public async Task LoadAsync_AfterUpdate_ShouldReturnLatestValues()
    {
        var service = CreateService(out _);
        var player = new TestPlayerVault { StorageId = "p1", Name = "Alice", Gold = 100 };

        service.MarkDirty(player, "owner1");

        player.Gold = 999;
        service.MarkDirty(player, "owner1");

        var loaded = await service.LoadAsync("p1");
        Assert.NotNull(loaded);
        Assert.Equal(999, loaded.Gold);
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

    [Fact]
    public async Task SaveAsync_ShouldUpdateCache()
    {
        var service = CreateService(out _);
        var player = new TestPlayerVault { StorageId = "p1", Name = "Alice", Gold = 500 };

        await service.SaveAsync(player);

        var loaded = await service.LoadAsync("p1");
        Assert.NotNull(loaded);
        Assert.Equal("Alice", loaded.Name);
        Assert.Equal(500, loaded.Gold);
    }

    [Fact]
    public void Dispose_ShouldNotThrow()
    {
        var service = CreateService(out _);
        service.MarkDirty(new TestPlayerVault { StorageId = "p1" }, "o1");

        service.Dispose(); // should not throw even with dirty entries
    }

    [Fact]
    public void AutoRegistersWithCoordinator()
    {
        var service = CreateService(out var coordinator);
        service.MarkDirty(new TestPlayerVault { StorageId = "p1" }, "o1");

        Assert.Equal(1, coordinator.TotalDirtyCount);
    }
}

// ═══════════════════════════════════════════════════════════════
//  INTEGRATION TESTS — AutosaveService with Mock Vault (DB)
// ═══════════════════════════════════════════════════════════════

public class AutosaveServiceWithVaultTests
{
    private AutosaveService<TestPlayerVault> CreateServiceWithVault(
        out Mock<IVault<TestPlayerVault>> mockVault,
        out AutosaveCoordinator coordinator,
        int batchSize = 100,
        bool walEnabled = false)
    {
        var cache = new InMemoryCache();
        coordinator = new AutosaveCoordinator(NullLoggerFactory.Instance);
        mockVault = new Mock<IVault<TestPlayerVault>>();
        return new AutosaveService<TestPlayerVault>(
            cache, coordinator, NullLoggerFactory.Instance,
            vault: mockVault.Object, batchSize: batchSize, walEnabled: walEnabled);
    }

    [Fact]
    public async Task FlushAsync_WithVault_ShouldCallSaveBatchAsync()
    {
        var service = CreateServiceWithVault(out var mockVault, out _);
        service.MarkDirty(new TestPlayerVault { StorageId = "p1", Name = "Alice" }, "o1");
        service.MarkDirty(new TestPlayerVault { StorageId = "p2", Name = "Bob" }, "o2");

        await service.FlushAsync();

        mockVault.Verify(v => v.SaveBatchAsync(
            It.Is<IEnumerable<TestPlayerVault>>(batch => batch.Count() == 2),
            It.IsAny<bool?>(), It.IsAny<CancellationToken>()), Times.Once);
        Assert.Equal(0, service.DirtyCount);
    }

    [Fact]
    public async Task FlushAsync_WithVault_BatchSize_ShouldChunk()
    {
        var service = CreateServiceWithVault(out var mockVault, out _, batchSize: 2);

        service.MarkDirty(new TestPlayerVault { StorageId = "p1" }, "o1");
        service.MarkDirty(new TestPlayerVault { StorageId = "p2" }, "o1");
        service.MarkDirty(new TestPlayerVault { StorageId = "p3" }, "o1");
        service.MarkDirty(new TestPlayerVault { StorageId = "p4" }, "o1");
        service.MarkDirty(new TestPlayerVault { StorageId = "p5" }, "o1");

        await service.FlushAsync();

        // 5 entities with batch size 2 = 3 batch calls (2+2+1)
        mockVault.Verify(v => v.SaveBatchAsync(
            It.IsAny<IEnumerable<TestPlayerVault>>(),
            It.IsAny<bool?>(), It.IsAny<CancellationToken>()), Times.Exactly(3));
        Assert.Equal(0, service.DirtyCount);
    }

    [Fact]
    public async Task FlushAsync_BatchFails_ShouldFallbackToIndividualSaves()
    {
        var service = CreateServiceWithVault(out var mockVault, out _);

        mockVault.Setup(v => v.SaveBatchAsync(
            It.IsAny<IEnumerable<TestPlayerVault>>(),
            It.IsAny<bool?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new Exception("batch failed"));

        mockVault.Setup(v => v.SaveAsync(
            It.IsAny<TestPlayerVault>(),
            It.IsAny<bool?>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        service.MarkDirty(new TestPlayerVault { StorageId = "p1" }, "o1");
        service.MarkDirty(new TestPlayerVault { StorageId = "p2" }, "o2");

        await service.FlushAsync();

        // Batch failed, should fall back to individual saves
        mockVault.Verify(v => v.SaveAsync(
            It.IsAny<TestPlayerVault>(),
            It.IsAny<bool?>(), It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    [Fact]
    public async Task SaveAsync_WithVault_ShouldCallVaultSave()
    {
        var service = CreateServiceWithVault(out var mockVault, out _);
        var player = new TestPlayerVault { StorageId = "p1" };

        await service.SaveAsync(player);

        mockVault.Verify(v => v.SaveAsync(
            player, It.IsAny<bool?>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task FlushByOwnerAsync_WithVault_ShouldOnlySaveOwnerEntities()
    {
        var service = CreateServiceWithVault(out var mockVault, out _);

        service.MarkDirty(new TestPlayerVault { StorageId = "p1" }, "owner1");
        service.MarkDirty(new TestPlayerVault { StorageId = "p2" }, "owner1");
        service.MarkDirty(new TestPlayerVault { StorageId = "p3" }, "owner2");

        await service.FlushByOwnerAsync("owner1");

        // Only 2 entities from owner1 should be saved
        mockVault.Verify(v => v.SaveBatchAsync(
            It.Is<IEnumerable<TestPlayerVault>>(batch => batch.Count() == 2),
            It.IsAny<bool?>(), It.IsAny<CancellationToken>()), Times.Once);
        Assert.Equal(1, service.DirtyCount); // owner2's p3 remains
    }

    [Fact]
    public async Task LoadAsync_CacheMiss_ShouldFallbackToVault()
    {
        var service = CreateServiceWithVault(out var mockVault, out _);
        var player = new TestPlayerVault { StorageId = "p1", Name = "FromDB", Gold = 9999 };

        // Setup vault query chain
        var queryMock = new Mock<IVault<TestPlayerVault>>();
        queryMock.Setup(v => v.FirstOrDefaultAsync(It.IsAny<CancellationToken>())).ReturnsAsync(player);
        mockVault.Setup(v => v.Where(It.IsAny<System.Linq.Expressions.Expression<Func<TestPlayerVault, bool>>>()))
            .Returns(queryMock.Object);

        var loaded = await service.LoadAsync("p1");

        Assert.NotNull(loaded);
        Assert.Equal("FromDB", loaded.Name);
        Assert.Equal(9999, loaded.Gold);
    }
}

// ═══════════════════════════════════════════════════════════════
//  UNIT TESTS — AutosaveCoordinator
// ═══════════════════════════════════════════════════════════════

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

    [Fact]
    public void TotalDirtyCount_MultipleServices_ShouldSumAll()
    {
        var cache = new InMemoryCache();
        var coordinator = new AutosaveCoordinator(NullLoggerFactory.Instance);

        var s1 = new AutosaveService<TestPlayerVault>(
            cache, coordinator, NullLoggerFactory.Instance, vault: null);
        var s2 = new AutosaveService<TestPlayerVault>(
            new InMemoryCache(), coordinator, NullLoggerFactory.Instance, vault: null);

        s1.MarkDirty(new TestPlayerVault { StorageId = "a" }, "o1");
        s1.MarkDirty(new TestPlayerVault { StorageId = "b" }, "o1");
        s2.MarkDirty(new TestPlayerVault { StorageId = "c" }, "o2");

        Assert.Equal(3, coordinator.TotalDirtyCount);
    }

    [Fact]
    public async Task FlushByOwnerAsync_ServiceThrows_ShouldNotStopOtherServices()
    {
        var coordinator = new AutosaveCoordinator(NullLoggerFactory.Instance);

        var mockService1 = new Mock<IAutosaveServiceBase>();
        mockService1.Setup(s => s.FlushByOwnerAsync("o1")).ThrowsAsync(new Exception("boom"));
        mockService1.Setup(s => s.DirtyCount).Returns(1);

        var mockService2 = new Mock<IAutosaveServiceBase>();
        mockService2.Setup(s => s.DirtyCount).Returns(1);

        coordinator.Register(mockService1.Object);
        coordinator.Register(mockService2.Object);

        // Should not throw — catches per-service exceptions
        await coordinator.FlushByOwnerAsync("o1");

        // Second service should still have been flushed
        mockService2.Verify(s => s.FlushByOwnerAsync("o1"), Times.Once);
    }

    [Fact]
    public async Task FlushAllAsync_ServiceThrows_ShouldNotStopOtherServices()
    {
        var coordinator = new AutosaveCoordinator(NullLoggerFactory.Instance);

        var mockService1 = new Mock<IAutosaveServiceBase>();
        mockService1.Setup(s => s.FlushAsync()).ThrowsAsync(new Exception("boom"));

        var mockService2 = new Mock<IAutosaveServiceBase>();

        coordinator.Register(mockService1.Object);
        coordinator.Register(mockService2.Object);

        await coordinator.FlushAllAsync();

        mockService2.Verify(s => s.FlushAsync(), Times.Once);
    }

    [Fact]
    public async Task FlushAllAsync_NoServices_ShouldBeNoOp()
    {
        var coordinator = new AutosaveCoordinator(NullLoggerFactory.Instance);
        await coordinator.FlushAllAsync(); // should not throw
        Assert.Equal(0, coordinator.TotalDirtyCount);
    }
}

// ═══════════════════════════════════════════════════════════════
//  UNIT + INTEGRATION TESTS — WriteAheadLog (WAL)
// ═══════════════════════════════════════════════════════════════

public class WriteAheadLogTests : IDisposable
{
    private readonly string _walDir;

    public WriteAheadLogTests()
    {
        _walDir = Path.Combine(Path.GetTempPath(), $"altruist_wal_test_{Guid.NewGuid():N}");
    }

    public void Dispose()
    {
        if (Directory.Exists(_walDir))
            Directory.Delete(_walDir, true);
    }

    [Fact]
    public void Append_ShouldBufferEntry()
    {
        using var wal = new WriteAheadLog<TestPlayerVault>(_walDir, 9999, NullLoggerFactory.Instance);
        var player = new TestPlayerVault { StorageId = "p1", Name = "Alice", Gold = 100 };

        wal.Append(player, "owner1");

        Assert.Equal(1, wal.BufferCount);
    }

    [Fact]
    public void Append_MultipleEntries_ShouldBufferAll()
    {
        using var wal = new WriteAheadLog<TestPlayerVault>(_walDir, 9999, NullLoggerFactory.Instance);

        for (int i = 0; i < 100; i++)
            wal.Append(new TestPlayerVault { StorageId = $"p{i}" }, "o1");

        Assert.Equal(100, wal.BufferCount);
    }

    [Fact]
    public void Append_ZeroDiskIO_FileNotCreated()
    {
        using var wal = new WriteAheadLog<TestPlayerVault>(_walDir, 9999, NullLoggerFactory.Instance);
        wal.Append(new TestPlayerVault { StorageId = "p1" }, "o1");

        // File should NOT exist — Append only buffers in memory
        Assert.False(File.Exists(wal.FilePath));
    }

    [Fact]
    public async Task FlushBufferToDisk_ShouldCreateWalFile()
    {
        using var wal = new WriteAheadLog<TestPlayerVault>(_walDir, 9999, NullLoggerFactory.Instance);
        var player = new TestPlayerVault { StorageId = "p1", Name = "Alice" };

        wal.Append(player, "owner1");
        await wal.FlushBufferToDiskAsync();

        Assert.True(File.Exists(wal.FilePath));
        var lines = await File.ReadAllLinesAsync(wal.FilePath);
        Assert.Single(lines);
        Assert.Contains("\"p1\"", lines[0]);
    }

    [Fact]
    public async Task FlushBufferToDisk_ShouldClearBuffer()
    {
        using var wal = new WriteAheadLog<TestPlayerVault>(_walDir, 9999, NullLoggerFactory.Instance);
        wal.Append(new TestPlayerVault { StorageId = "p1" }, "o1");
        wal.Append(new TestPlayerVault { StorageId = "p2" }, "o2");

        Assert.Equal(2, wal.BufferCount);
        await wal.FlushBufferToDiskAsync();
        Assert.Equal(0, wal.BufferCount);
    }

    [Fact]
    public async Task FlushBufferToDisk_EmptyBuffer_ShouldBeNoOp()
    {
        using var wal = new WriteAheadLog<TestPlayerVault>(_walDir, 9999, NullLoggerFactory.Instance);
        await wal.FlushBufferToDiskAsync();

        Assert.False(File.Exists(wal.FilePath));
    }

    [Fact]
    public async Task FlushBufferToDisk_MultipleFlushes_ShouldAppend()
    {
        using var wal = new WriteAheadLog<TestPlayerVault>(_walDir, 9999, NullLoggerFactory.Instance);

        wal.Append(new TestPlayerVault { StorageId = "p1" }, "o1");
        await wal.FlushBufferToDiskAsync();

        wal.Append(new TestPlayerVault { StorageId = "p2" }, "o2");
        await wal.FlushBufferToDiskAsync();

        var lines = await File.ReadAllLinesAsync(wal.FilePath);
        Assert.Equal(2, lines.Length);
    }

    [Fact]
    public async Task FlushBufferToDisk_WalFileFormat_ShouldBeNewlineDelimitedJson()
    {
        using var wal = new WriteAheadLog<TestPlayerVault>(_walDir, 9999, NullLoggerFactory.Instance);
        wal.Append(new TestPlayerVault { StorageId = "p1", Name = "Alice", Gold = 500 }, "owner1");
        await wal.FlushBufferToDiskAsync();

        var line = (await File.ReadAllLinesAsync(wal.FilePath))[0];
        var entry = JsonSerializer.Deserialize<WalEntry>(line);

        Assert.NotNull(entry);
        Assert.Equal("p1", entry.StorageId);
        Assert.Equal("owner1", entry.OwnerId);
        Assert.Contains("Alice", entry.Data);
        Assert.Contains("500", entry.Data);
    }

    [Fact]
    public async Task Truncate_ShouldDeleteWalFile()
    {
        using var wal = new WriteAheadLog<TestPlayerVault>(_walDir, 9999, NullLoggerFactory.Instance);
        wal.Append(new TestPlayerVault { StorageId = "p1" }, "owner1");
        await wal.FlushBufferToDiskAsync();
        Assert.True(File.Exists(wal.FilePath));

        await wal.TruncateAsync();

        Assert.False(File.Exists(wal.FilePath));
    }

    [Fact]
    public async Task Truncate_ShouldDrainBuffer()
    {
        using var wal = new WriteAheadLog<TestPlayerVault>(_walDir, 9999, NullLoggerFactory.Instance);
        wal.Append(new TestPlayerVault { StorageId = "p1" }, "o1");
        wal.Append(new TestPlayerVault { StorageId = "p2" }, "o2");

        // Don't flush to disk, just truncate — buffer should be drained
        await wal.TruncateAsync();

        Assert.Equal(0, wal.BufferCount);
    }

    [Fact]
    public async Task Truncate_NoFile_ShouldNotThrow()
    {
        using var wal = new WriteAheadLog<TestPlayerVault>(_walDir, 9999, NullLoggerFactory.Instance);
        await wal.TruncateAsync(); // no file exists — should be fine
    }

    [Fact]
    public async Task Recover_ShouldReturnEntries_AndDeduplicateByStorageId()
    {
        using var wal = new WriteAheadLog<TestPlayerVault>(_walDir, 9999, NullLoggerFactory.Instance);

        // Write same entity twice (simulating two MarkDirty calls)
        wal.Append(new TestPlayerVault { StorageId = "p1", Name = "Alice", Gold = 100 }, "o1");
        wal.Append(new TestPlayerVault { StorageId = "p1", Name = "Alice", Gold = 200 }, "o1");
        wal.Append(new TestPlayerVault { StorageId = "p2", Name = "Bob" }, "o2");
        await wal.FlushBufferToDiskAsync();

        var entries = await wal.RecoverAsync();

        Assert.Equal(2, entries.Count); // p1 deduplicated, p2 separate
        var p1Entry = entries.First(e => e.StorageId == "p1");
        Assert.Contains("200", p1Entry.Data); // Last write wins
    }

    [Fact]
    public async Task Recover_ShouldReturnEmpty_WhenNoWalFile()
    {
        using var wal = new WriteAheadLog<TestPlayerVault>(_walDir, 9999, NullLoggerFactory.Instance);

        var entries = await wal.RecoverAsync();

        Assert.Empty(entries);
    }

    [Fact]
    public async Task Recover_CorruptLine_ShouldSkipAndContinue()
    {
        using var wal = new WriteAheadLog<TestPlayerVault>(_walDir, 9999, NullLoggerFactory.Instance);
        wal.Append(new TestPlayerVault { StorageId = "p1", Name = "Alice" }, "o1");
        await wal.FlushBufferToDiskAsync();

        // Inject a corrupt line
        await File.AppendAllLinesAsync(wal.FilePath, new[] { "THIS IS NOT JSON", "" });

        // Append another valid entry
        wal.Append(new TestPlayerVault { StorageId = "p2", Name = "Bob" }, "o2");
        await wal.FlushBufferToDiskAsync();

        var entries = await wal.RecoverAsync();

        // Should recover p1 and p2, skip the corrupt line
        Assert.Equal(2, entries.Count);
    }

    [Fact]
    public async Task Recover_EmptyLines_ShouldBeSkipped()
    {
        using var wal = new WriteAheadLog<TestPlayerVault>(_walDir, 9999, NullLoggerFactory.Instance);
        wal.Append(new TestPlayerVault { StorageId = "p1" }, "o1");
        await wal.FlushBufferToDiskAsync();

        // Inject empty lines
        await File.AppendAllLinesAsync(wal.FilePath, new[] { "", "   ", "" });

        var entries = await wal.RecoverAsync();
        Assert.Single(entries);
    }

    [Fact]
    public async Task FlushThenTruncate_ShouldClearBuffer()
    {
        using var wal = new WriteAheadLog<TestPlayerVault>(_walDir, 9999, NullLoggerFactory.Instance);
        wal.Append(new TestPlayerVault { StorageId = "p1" }, "o1");
        wal.Append(new TestPlayerVault { StorageId = "p2" }, "o2");

        await wal.FlushBufferToDiskAsync();
        await wal.TruncateAsync();

        Assert.Equal(0, wal.BufferCount);
        Assert.False(File.Exists(wal.FilePath));
    }

    [Fact]
    public async Task Recover_LargeWal_ShouldHandleManyEntries()
    {
        using var wal = new WriteAheadLog<TestPlayerVault>(_walDir, 9999, NullLoggerFactory.Instance);

        for (int i = 0; i < 1000; i++)
            wal.Append(new TestPlayerVault { StorageId = $"p{i}", Gold = i }, "o1");

        await wal.FlushBufferToDiskAsync();

        var entries = await wal.RecoverAsync();
        Assert.Equal(1000, entries.Count);
    }

    [Fact]
    public async Task WalFilePath_ShouldBeNamedByType()
    {
        using var wal = new WriteAheadLog<TestPlayerVault>(_walDir, 9999, NullLoggerFactory.Instance);
        Assert.EndsWith("TestPlayerVault.wal", wal.FilePath);
    }

    [Fact]
    public void WalDirectory_ShouldBeCreated()
    {
        var dir = Path.Combine(_walDir, "nested", "deep");
        using var wal = new WriteAheadLog<TestPlayerVault>(dir, 9999, NullLoggerFactory.Instance);
        Assert.True(Directory.Exists(dir));
    }
}

// ═══════════════════════════════════════════════════════════════
//  INTEGRATION TESTS — AutosaveService with WAL enabled
// ═══════════════════════════════════════════════════════════════

public class AutosaveServiceWithWalTests : IDisposable
{
    private readonly string _walDir;

    public AutosaveServiceWithWalTests()
    {
        _walDir = Path.Combine(Path.GetTempPath(), $"altruist_wal_integ_{Guid.NewGuid():N}");
    }

    public void Dispose()
    {
        if (Directory.Exists(_walDir))
            Directory.Delete(_walDir, true);
    }

    private AutosaveService<TestPlayerVault> CreateServiceWithWal(
        out AutosaveCoordinator coordinator,
        IVault<TestPlayerVault>? vault = null,
        int batchSize = 100)
    {
        var cache = new InMemoryCache();
        coordinator = new AutosaveCoordinator(NullLoggerFactory.Instance);
        return new AutosaveService<TestPlayerVault>(
            cache, coordinator, NullLoggerFactory.Instance,
            vault: vault, batchSize: batchSize,
            walEnabled: true, walDirectory: _walDir, walFlushIntervalSeconds: 9999);
    }

    [Fact]
    public void MarkDirty_WithWal_ShouldAppendToWalBuffer()
    {
        var service = CreateServiceWithWal(out _);
        service.MarkDirty(new TestPlayerVault { StorageId = "p1", Name = "Alice" }, "o1");

        // WAL file shouldn't exist yet — only buffered
        var walFile = Path.Combine(_walDir, "TestPlayerVault.wal");
        Assert.False(File.Exists(walFile));

        Assert.Equal(1, service.DirtyCount);
    }

    [Fact]
    public async Task FlushAsync_WithWal_ShouldTruncateWalAfterSuccess()
    {
        var service = CreateServiceWithWal(out _);
        service.MarkDirty(new TestPlayerVault { StorageId = "p1" }, "o1");

        // Manually flush WAL buffer to disk first to simulate timer
        var walFile = Path.Combine(_walDir, "TestPlayerVault.wal");

        await service.FlushAsync();

        // WAL should be truncated after successful flush
        Assert.False(File.Exists(walFile));
        Assert.Equal(0, service.DirtyCount);
    }

    [Fact]
    public async Task RecoverFromWalAsync_NoWal_ShouldReturnZero()
    {
        var service = CreateServiceWithWal(out _);

        var recovered = await service.RecoverFromWalAsync();
        Assert.Equal(0, recovered);
    }

    [Fact]
    public async Task RecoverFromWalAsync_WithEntries_ShouldSaveToVault()
    {
        var mockVault = new Mock<IVault<TestPlayerVault>>();
        var service = CreateServiceWithWal(out _, vault: mockVault.Object);

        // Simulate: write entries, flush WAL to disk, then "crash" (don't flush to DB)
        service.MarkDirty(new TestPlayerVault { StorageId = "p1", Name = "Alice", Gold = 100 }, "o1");
        service.MarkDirty(new TestPlayerVault { StorageId = "p2", Name = "Bob", Gold = 200 }, "o2");

        // Manually write WAL entries to disk (simulating the timer flush)
        var walFile = Path.Combine(_walDir, "TestPlayerVault.wal");
        var entries = new[]
        {
            new WalEntry(typeof(TestPlayerVault).AssemblyQualifiedName!, "p1", "o1",
                JsonSerializer.Serialize(new TestPlayerVault { StorageId = "p1", Name = "Alice", Gold = 100 })),
            new WalEntry(typeof(TestPlayerVault).AssemblyQualifiedName!, "p2", "o2",
                JsonSerializer.Serialize(new TestPlayerVault { StorageId = "p2", Name = "Bob", Gold = 200 }))
        };
        Directory.CreateDirectory(_walDir);
        await File.WriteAllLinesAsync(walFile, entries.Select(e => JsonSerializer.Serialize(e)));

        // Now create a fresh service (simulating server restart)
        var cache2 = new InMemoryCache();
        var coordinator2 = new AutosaveCoordinator(NullLoggerFactory.Instance);
        var service2 = new AutosaveService<TestPlayerVault>(
            cache2, coordinator2, NullLoggerFactory.Instance,
            vault: mockVault.Object, walEnabled: true,
            walDirectory: _walDir, walFlushIntervalSeconds: 9999);

        var recovered = await service2.RecoverFromWalAsync();

        Assert.Equal(2, recovered);
        mockVault.Verify(v => v.SaveBatchAsync(
            It.Is<IEnumerable<TestPlayerVault>>(batch => batch.Count() == 2),
            It.IsAny<bool?>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task RecoverFromWalAsync_BatchFails_ShouldFallbackToIndividual()
    {
        var mockVault = new Mock<IVault<TestPlayerVault>>();
        mockVault.Setup(v => v.SaveBatchAsync(
            It.IsAny<IEnumerable<TestPlayerVault>>(),
            It.IsAny<bool?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new Exception("batch failed"));
        mockVault.Setup(v => v.SaveAsync(
            It.IsAny<TestPlayerVault>(),
            It.IsAny<bool?>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        // Write WAL entries to disk
        Directory.CreateDirectory(_walDir);
        var walFile = Path.Combine(_walDir, "TestPlayerVault.wal");
        var entries = new[]
        {
            new WalEntry(typeof(TestPlayerVault).AssemblyQualifiedName!, "p1", "o1",
                JsonSerializer.Serialize(new TestPlayerVault { StorageId = "p1", Name = "Alice" })),
            new WalEntry(typeof(TestPlayerVault).AssemblyQualifiedName!, "p2", "o2",
                JsonSerializer.Serialize(new TestPlayerVault { StorageId = "p2", Name = "Bob" }))
        };
        await File.WriteAllLinesAsync(walFile, entries.Select(e => JsonSerializer.Serialize(e)));

        var cache = new InMemoryCache();
        var coordinator = new AutosaveCoordinator(NullLoggerFactory.Instance);
        var service = new AutosaveService<TestPlayerVault>(
            cache, coordinator, NullLoggerFactory.Instance,
            vault: mockVault.Object, walEnabled: true,
            walDirectory: _walDir, walFlushIntervalSeconds: 9999);

        var recovered = await service.RecoverFromWalAsync();

        Assert.Equal(2, recovered);
        // Should fallback to individual saves
        mockVault.Verify(v => v.SaveAsync(
            It.IsAny<TestPlayerVault>(),
            It.IsAny<bool?>(), It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    [Fact]
    public async Task RecoverFromWalAsync_NoVault_ShouldStillReturnCount()
    {
        // Write WAL entries to disk
        Directory.CreateDirectory(_walDir);
        var walFile = Path.Combine(_walDir, "TestPlayerVault.wal");
        var entry = new WalEntry(typeof(TestPlayerVault).AssemblyQualifiedName!, "p1", "o1",
            JsonSerializer.Serialize(new TestPlayerVault { StorageId = "p1" }));
        await File.WriteAllLinesAsync(walFile, new[] { JsonSerializer.Serialize(entry) });

        // Service without vault
        var service = CreateServiceWithWal(out _, vault: null);

        var recovered = await service.RecoverFromWalAsync();

        // Still reports recovered count even without vault
        Assert.Equal(1, recovered);
    }

    [Fact]
    public async Task RecoverFromWalAsync_ShouldTruncateWalAfterRecovery()
    {
        var mockVault = new Mock<IVault<TestPlayerVault>>();

        Directory.CreateDirectory(_walDir);
        var walFile = Path.Combine(_walDir, "TestPlayerVault.wal");
        var entry = new WalEntry(typeof(TestPlayerVault).AssemblyQualifiedName!, "p1", "o1",
            JsonSerializer.Serialize(new TestPlayerVault { StorageId = "p1" }));
        await File.WriteAllLinesAsync(walFile, new[] { JsonSerializer.Serialize(entry) });

        var service = CreateServiceWithWal(out _, vault: mockVault.Object);
        await service.RecoverFromWalAsync();

        // WAL file should be deleted after recovery
        Assert.False(File.Exists(walFile));
    }
}

// ═══════════════════════════════════════════════════════════════
//  UNIT TESTS — AutosaveService with WAL disabled
// ═══════════════════════════════════════════════════════════════

public class AutosaveServiceWalDisabledTests
{
    [Fact]
    public async Task RecoverFromWalAsync_WalDisabled_ShouldReturnZero()
    {
        var cache = new InMemoryCache();
        var coordinator = new AutosaveCoordinator(NullLoggerFactory.Instance);
        var service = new AutosaveService<TestPlayerVault>(
            cache, coordinator, NullLoggerFactory.Instance,
            vault: null, walEnabled: false);

        var recovered = await service.RecoverFromWalAsync();
        Assert.Equal(0, recovered);
    }

    [Fact]
    public void MarkDirty_WalDisabled_ShouldStillTrackDirty()
    {
        var cache = new InMemoryCache();
        var coordinator = new AutosaveCoordinator(NullLoggerFactory.Instance);
        var service = new AutosaveService<TestPlayerVault>(
            cache, coordinator, NullLoggerFactory.Instance,
            vault: null, walEnabled: false);

        service.MarkDirty(new TestPlayerVault { StorageId = "p1" }, "o1");
        Assert.Equal(1, service.DirtyCount);
    }

    [Fact]
    public async Task FlushAsync_WalDisabled_ShouldStillClearDirty()
    {
        var cache = new InMemoryCache();
        var coordinator = new AutosaveCoordinator(NullLoggerFactory.Instance);
        var service = new AutosaveService<TestPlayerVault>(
            cache, coordinator, NullLoggerFactory.Instance,
            vault: null, walEnabled: false);

        service.MarkDirty(new TestPlayerVault { StorageId = "p1" }, "o1");
        await service.FlushAsync();

        Assert.Equal(0, service.DirtyCount);
    }
}

// ═══════════════════════════════════════════════════════════════
//  UNIT TESTS — AutosaveAttribute
// ═══════════════════════════════════════════════════════════════

public class AutosaveAttributeTests
{
    [Fact]
    public void CronConstructor_ShouldSetCronExpression()
    {
        var attr = new AutosaveAttribute("*/10 * * * *");
        Assert.Equal("*/10 * * * *", attr.CronExpression);
        Assert.False(attr.UsesDefaultInterval);
    }

    [Fact]
    public void TimeBasedConstructor_ShouldSetIntervalAndUnit()
    {
        var attr = new AutosaveAttribute(120, AutosaveCycle.Seconds);
        Assert.Equal(120, attr.IntervalValue);
        Assert.Equal(AutosaveCycle.Seconds, attr.Unit);
        Assert.False(attr.UsesDefaultInterval);
    }

    [Fact]
    public void DefaultConstructor_ShouldUseDefaultInterval()
    {
        var attr = new AutosaveAttribute();
        Assert.True(attr.UsesDefaultInterval);
    }

    [Fact]
    public void GetTimeSpan_Seconds()
    {
        var attr = new AutosaveAttribute(30, AutosaveCycle.Seconds);
        Assert.Equal(TimeSpan.FromSeconds(30), attr.GetTimeSpan());
    }

    [Fact]
    public void GetTimeSpan_Minutes()
    {
        var attr = new AutosaveAttribute(5, AutosaveCycle.Minutes);
        Assert.Equal(TimeSpan.FromMinutes(5), attr.GetTimeSpan());
    }

    [Fact]
    public void GetTimeSpan_Hours()
    {
        var attr = new AutosaveAttribute(2, AutosaveCycle.Hours);
        Assert.Equal(TimeSpan.FromHours(2), attr.GetTimeSpan());
    }

    [Fact]
    public void GetTimeSpan_CronBased_ShouldReturnNull()
    {
        var attr = new AutosaveAttribute("*/5 * * * *");
        Assert.Null(attr.GetTimeSpan());
    }

    [Fact]
    public void BatchSize_Default_ShouldBe100()
    {
        var attr = new AutosaveAttribute();
        Assert.Equal(100, attr.BatchSize);
    }

    [Fact]
    public void Wal_Default_ShouldBeTrue()
    {
        var attr = new AutosaveAttribute();
        Assert.True(attr.Wal);
    }

    [Fact]
    public void Wal_CanBeDisabled()
    {
        var attr = new AutosaveAttribute { Wal = false };
        Assert.False(attr.Wal);
    }
}

// ═══════════════════════════════════════════════════════════════
//  UNIT TESTS — AutosaveServiceFactory
// ═══════════════════════════════════════════════════════════════

public class AutosaveServiceFactoryTests
{
    [Fact]
    public void CanCreate_AutosaveServiceType_ShouldReturnTrue()
    {
        var factory = new AutosaveServiceFactory();
        Assert.True(factory.CanCreate(typeof(IAutosaveService<TestPlayerVault>)));
    }

    [Fact]
    public void CanCreate_NonAutosaveModel_ShouldReturnFalse()
    {
        var factory = new AutosaveServiceFactory();
        // VaultModel without [Autosave] attribute — factory should reject
        Assert.False(factory.CanCreate(typeof(IAutosaveService<VaultModelWithoutAutosave>)));
    }

    [Fact]
    public void CanCreate_NonGenericType_ShouldReturnFalse()
    {
        var factory = new AutosaveServiceFactory();
        Assert.False(factory.CanCreate(typeof(string)));
    }

    [Fact]
    public void CanCreate_WrongGenericType_ShouldReturnFalse()
    {
        var factory = new AutosaveServiceFactory();
        Assert.False(factory.CanCreate(typeof(List<TestPlayerVault>)));
    }

    [Fact]
    public void ResolveInterval_ExplicitCron_ShouldReturnCron()
    {
        var attr = new AutosaveAttribute("*/10 * * * *");
        Assert.Equal("*/10 * * * *", AutosaveServiceFactory.ResolveInterval(attr, null));
    }

    [Fact]
    public void ResolveInterval_DefaultAttribute_NoConfig_ShouldReturnFallback()
    {
        var attr = new AutosaveAttribute();
        Assert.Equal(AutosaveServiceFactory.FallbackInterval, AutosaveServiceFactory.ResolveInterval(attr, null));
    }

    [Fact]
    public void ResolveBatchSize_DefaultAttribute_NoConfig_ShouldReturnFallback()
    {
        var attr = new AutosaveAttribute();
        Assert.Equal(AutosaveServiceFactory.FallbackBatchSize, AutosaveServiceFactory.ResolveBatchSize(attr, null));
    }

    [Fact]
    public void ResolveBatchSize_ExplicitAttribute_ShouldOverride()
    {
        var attr = new AutosaveAttribute { BatchSize = 50 };
        Assert.Equal(50, AutosaveServiceFactory.ResolveBatchSize(attr, null));
    }

    [Fact]
    public void ResolveWalEnabled_NoConfig_ShouldReturnTrue()
    {
        Assert.True(AutosaveServiceFactory.ResolveWalEnabled(null));
    }
}

// Helper model without [Autosave] attribute
public class VaultModelWithoutAutosave : VaultModel
{
    public override string StorageId { get; set; } = "";
}

// ═══════════════════════════════════════════════════════════════
//  CONCURRENCY TESTS
// ═══════════════════════════════════════════════════════════════

public class AutosaveConcurrencyTests
{
    [Fact]
    public void MarkDirty_ConcurrentWrites_ShouldBeThreadSafe()
    {
        var cache = new InMemoryCache();
        var coordinator = new AutosaveCoordinator(NullLoggerFactory.Instance);
        var service = new AutosaveService<TestPlayerVault>(
            cache, coordinator, NullLoggerFactory.Instance, vault: null, walEnabled: false);

        Parallel.For(0, 500, i =>
        {
            service.MarkDirty(new TestPlayerVault { StorageId = $"p{i}" }, $"owner{i % 10}");
        });

        Assert.Equal(500, service.DirtyCount);
    }

    [Fact]
    public async Task FlushAndMarkDirty_Concurrent_ShouldNotThrow()
    {
        var cache = new InMemoryCache();
        var coordinator = new AutosaveCoordinator(NullLoggerFactory.Instance);
        var service = new AutosaveService<TestPlayerVault>(
            cache, coordinator, NullLoggerFactory.Instance, vault: null, walEnabled: false);

        var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var tasks = new List<Task>();

        // Writer task
        tasks.Add(Task.Run(async () =>
        {
            int i = 0;
            while (!cts.IsCancellationRequested)
            {
                service.MarkDirty(new TestPlayerVault { StorageId = $"p{i++}" }, "o1");
                await Task.Yield();
            }
        }));

        // Flusher task
        tasks.Add(Task.Run(async () =>
        {
            while (!cts.IsCancellationRequested)
            {
                await service.FlushAsync();
                await Task.Yield();
            }
        }));

        await Task.WhenAll(tasks);
        // If we got here without exceptions, concurrency is safe
    }

    [Fact]
    public void WalAppend_ConcurrentWrites_ShouldBeThreadSafe()
    {
        var walDir = Path.Combine(Path.GetTempPath(), $"altruist_wal_conc_{Guid.NewGuid():N}");
        try
        {
            using var wal = new WriteAheadLog<TestPlayerVault>(walDir, 9999, NullLoggerFactory.Instance);

            Parallel.For(0, 500, i =>
            {
                wal.Append(new TestPlayerVault { StorageId = $"p{i}" }, $"o{i}");
            });

            Assert.Equal(500, wal.BufferCount);
        }
        finally
        {
            if (Directory.Exists(walDir))
                Directory.Delete(walDir, true);
        }
    }
}
