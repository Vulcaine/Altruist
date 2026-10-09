using Altruist;
using Altruist.Gaming.Autosave;
using Altruist.InMemory;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Tests.Gaming.Autosave;

public class AutosaveWalRegressionTests : IDisposable
{
    private readonly string _walDir = Path.Combine(Path.GetTempPath(), $"altruist_wal_regression_{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_walDir))
            Directory.Delete(_walDir, true);
    }

    [Fact]
    public async Task Flush_keeps_the_wal_when_a_save_failed()
    {
        var vault = new Mock<IVault<TestPlayerVault>>();
        vault.Setup(v => v.SaveBatchAsync(It.IsAny<IEnumerable<TestPlayerVault>>(), It.IsAny<bool?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("db down"));
        vault.Setup(v => v.SaveAsync(It.IsAny<TestPlayerVault>(), It.IsAny<bool?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("db down"));
        var service = new AutosaveService<TestPlayerVault>(
            new InMemoryCache(), new AutosaveCoordinator(NullLoggerFactory.Instance), NullLoggerFactory.Instance,
            vault: vault.Object, walEnabled: true, walDirectory: _walDir, walFlushIntervalSeconds: 9999);
        service.MarkDirty(new TestPlayerVault { StorageId = "p1", Name = "Alice" }, "o1");

        await service.FlushAsync();
        service.Dispose();

        Assert.Equal(1, service.DirtyCount);
        using var wal = new WriteAheadLog<TestPlayerVault>(_walDir, 9999, NullLoggerFactory.Instance);
        Assert.Single(await wal.RecoverAsync());
    }

    [Fact]
    public void Dispose_writes_buffered_entries_to_disk()
    {
        var wal = new WriteAheadLog<TestPlayerVault>(_walDir, 9999, NullLoggerFactory.Instance);
        wal.Append(new TestPlayerVault { StorageId = "p1", Name = "Alice" }, "o1");

        wal.Dispose();

        using var reopened = new WriteAheadLog<TestPlayerVault>(_walDir, 9999, NullLoggerFactory.Instance);
        var entries = reopened.RecoverAsync().GetAwaiter().GetResult();
        Assert.Single(entries);
    }
}
