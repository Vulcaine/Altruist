using Altruist.Networking;
using BenchmarkDotNet.Attributes;

namespace Altruist.Benchmarks;

/// <summary>
/// Benchmarks the entity sync delta detection system.
/// This is the hottest path in the framework — runs every tick for every [Synchronized] entity.
/// Measures: reflection metadata caching, property change detection, bitmask generation.
/// </summary>
[MemoryDiagnoser]
[SimpleJob(warmupCount: 5, iterationCount: 20)]
public class SyncBenchmarks
{
    private SyncEntity _entity = null!;
    private string _clientId = null!;

    [Synchronized]
    public class SyncEntity : ISynchronizedEntity
    {
        public string ClientId { get; set; } = "";

        [Synced(0, SyncAlways: true)]
        public string Name { get; set; } = "Player1";

        [Synced(1)]
        public int Hp { get; set; } = 100;

        [Synced(2)]
        public int MaxHp { get; set; } = 100;

        [Synced(3)]
        public int X { get; set; }

        [Synced(4)]
        public int Y { get; set; }

        [Synced(5)]
        public int Z { get; set; }

        [Synced(6)]
        public byte Level { get; set; } = 1;

        [Synced(7)]
        public int Gold { get; set; }

        [Synced(8)]
        public byte Empire { get; set; } = 1;

        [Synced(9)]
        public bool IsDead { get; set; }
    }

    [GlobalSetup]
    public void Setup()
    {
        _clientId = Guid.NewGuid().ToString("N");
        _entity = new SyncEntity { ClientId = _clientId, X = 100, Y = 200, Z = 0 };
        // First call populates metadata cache + baseline state
        Synchronization.GetChangedData(_entity, _clientId, 1);
    }

    [Benchmark(Description = "GetChangedData - no changes (SyncAlways only)")]
    public void NoChanges()
    {
        var (masks, maskCount, data) = Synchronization.GetChangedData(_entity, _clientId, 100);
        System.Buffers.ArrayPool<ulong>.Shared.Return(masks);
    }

    [Benchmark(Description = "GetChangedData - position changed (X+Y)")]
    public void PositionChanged()
    {
        _entity.X++;
        _entity.Y++;
        var (masks, maskCount, data) = Synchronization.GetChangedData(_entity, _clientId, 100);
        System.Buffers.ArrayPool<ulong>.Shared.Return(masks);
    }

    [Benchmark(Description = "GetChangedData - all properties changed")]
    public void AllChanged()
    {
        _entity.Hp--;
        _entity.X++;
        _entity.Y++;
        _entity.Z++;
        _entity.Gold++;
        _entity.Level++;
        _entity.IsDead = !_entity.IsDead;
        var (masks, maskCount, data) = Synchronization.GetChangedData(_entity, _clientId, 100);
        System.Buffers.ArrayPool<ulong>.Shared.Return(masks);
    }

    [Benchmark(Description = "GetChangedData - forceAll (full resync)")]
    public void ForceAll()
    {
        var (masks, maskCount, data) = Synchronization.GetChangedData(_entity, _clientId, 100, forceAllAsChanged: true);
        System.Buffers.ArrayPool<ulong>.Shared.Return(masks);
    }

    [Benchmark(Description = "SyncMetadata lookup (cached)")]
    public void MetadataLookup()
    {
        SyncMetadataHelper.GetSyncMetadata(typeof(SyncEntity));
    }
}
