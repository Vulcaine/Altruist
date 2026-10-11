using Altruist.Gaming;
using Tests.Gaming;

namespace Tests.Gaming.World;

public class PresenceZoneManagerTests
{
    private sealed class GatedSpawnHandler : IZoneSpawnHandler
    {
        public TaskCompletionSource<List<string>> Spawn = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int SpawnCalls;
        public readonly List<List<string>> Despawned = new();

        public Task<List<string>> SpawnZone(IManagedZone zone)
        {
            Interlocked.Increment(ref SpawnCalls);
            return Spawn.Task;
        }

        public Task DespawnZone(IManagedZone zone, IReadOnlyCollection<string> instanceIds)
        {
            lock (Despawned) Despawned.Add(instanceIds.ToList());
            return Task.CompletedTask;
        }
    }

    private static async Task Until(Func<bool> condition)
    {
        for (var i = 0; i < 200 && !condition(); i++) await Task.Delay(10);
        Assert.True(condition());
    }

    [Fact]
    public async Task Spawn_finishing_after_the_zone_was_deactivated_is_despawned()
    {
        var handler = new GatedSpawnHandler();
        var manager = new ZoneManager(handler);
        manager.RegisterZone("forest", []);

        await manager.PlayerEnteredZone("forest", "p1");
        await manager.PlayerLeftZone("forest", "p1");
        handler.Spawn.SetResult(["mob-1", "mob-2"]);

        await Until(() => { lock (handler.Despawned) return handler.Despawned.Any(d => d.Count == 2); });
        Assert.Equal(0, manager.TotalSpawnedEntities);
        Assert.Empty(manager.GetZone("forest")!.SpawnedInstanceIds);
    }

    [Fact]
    public async Task Failed_spawn_is_logged_and_the_next_enter_retries_while_players_are_inside()
    {
        var handler = new GatedSpawnHandler();
        var logs = new CapturingLoggerFactory();
        var manager = new ZoneManager(handler, logs);
        manager.RegisterZone("forest", []);

        await manager.PlayerEnteredZone("forest", "p1");
        handler.Spawn.SetException(new InvalidOperationException("boom"));
        await Until(() => !manager.GetZone("forest")!.IsActive);
        await Until(() => { return logs.Errors.Any(e => e is InvalidOperationException); });

        handler.Spawn = new(TaskCreationOptions.RunContinuationsAsynchronously);
        await manager.PlayerEnteredZone("forest", "p2");

        Assert.Equal(2, handler.SpawnCalls);
        Assert.True(manager.GetZone("forest")!.IsActive);
    }
}

public class CellAttributeTests
{
    [Fact]
    public void Safe_area_flag_has_a_generic_name()
    {
        Assert.Equal((byte)0x04, (byte)CellAttribute.SafeArea);
    }
}
