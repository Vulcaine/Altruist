using System.Numerics;
using Altruist.Gaming;
using Altruist.Gaming.ThreeD;
using Altruist.Networking;
using Altruist.ThreeD.Numerics;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Tests.Gaming.World.ThreeD;

[Synchronized]
public class TestSyncWorldObj : WorldObject3D, ISynchronizedEntity
{
    public string ClientId { get; set; } = "";

    [Synced(0, SyncAlways: true)]
    public string Name { get; set; } = "test";

    [Synced(1)]
    public int Hp { get; set; } = 100;

    public TestSyncWorldObj(float x, float y, string clientId)
        : base(Transform3D.From(new Vector3(x, y, 0), Quaternion.Identity, Vector3.One))
    {
        ClientId = clientId;
    }

    public override void Step(float dt, IGameWorldManager3D world) { }
}

public class EntitySyncServiceTests
{
    private WorldSnapshot CreateSnapshot(params ITypelessWorldObject[] objects)
    {
        var list = objects.ToList();
        var lookup = objects.ToDictionary(o => o.InstanceId, o => o);
        return new WorldSnapshot(0, list, lookup);
    }

    [Fact]
    public async Task Tick_ShouldNotThrow_WhenNoSendersConfigured()
    {
        var service = new EntitySyncService(NullLoggerFactory.Instance);

        var obj = new TestSyncWorldObj(0, 0, "player1");
        var snapshot = CreateSnapshot(obj);

        var ex = await Record.ExceptionAsync(() => service.Tick([snapshot], 25f));
        Assert.Null(ex);
    }

    [Fact]
    public async Task Tick_ShouldSkipNonSynchronizedEntities()
    {
        // TestWorldObj doesn't have [Synchronized] attribute
        var plainObj = new TestWorldObj(0, 0, clientId: "player1");
        var snapshot = CreateSnapshot(plainObj);

        var service = new EntitySyncService(NullLoggerFactory.Instance);
        var ex = await Record.ExceptionAsync(() => service.Tick([snapshot], 25f));
        Assert.Null(ex);
    }

    [Fact]
    public async Task Tick_ShouldSkipEntitiesWithEmptyClientId()
    {
        var service = new EntitySyncService(NullLoggerFactory.Instance);
        var obj = new TestSyncWorldObj(0, 0, ""); // empty ClientId
        var snapshot = CreateSnapshot(obj);

        var ex = await Record.ExceptionAsync(() => service.Tick([snapshot], 25f));
        Assert.Null(ex);
    }

    [Fact]
    public async Task Tick_WithEmptySnapshots_ShouldNotThrow()
    {
        var service = new EntitySyncService(NullLoggerFactory.Instance);

        var ex = await Record.ExceptionAsync(() => service.Tick([], 25f));
        Assert.Null(ex);
    }
}
