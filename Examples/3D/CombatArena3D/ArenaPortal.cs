using System.Numerics;
using Altruist;
using Altruist.Gaming.Combat;
using Altruist.Gaming.ThreeD;
using Altruist.ThreeD.Numerics;
using Microsoft.Extensions.Logging;

namespace CombatArena3D;

[Portal("/arena")]
public class ArenaPortal : Portal, OnConnectedAsync, OnDisconnectedAsync
{
    private readonly IAltruistRouter _router;
    private readonly IGameWorldOrganizer3D _worlds;
    private readonly ICombatService _combat;
    private readonly ILogger _logger;
    private static uint _nextVid = 1;

    public ArenaPortal(
        IAltruistRouter router,
        IGameWorldOrganizer3D worlds,
        ICombatService combat,
        ILoggerFactory loggerFactory)
    {
        _router = router;
        _worlds = worlds;
        _combat = combat;
        _logger = loggerFactory.CreateLogger<ArenaPortal>();
    }

    public Task OnConnectedAsync(string clientId, ConnectionManager manager, AltruistConnection connection)
    {
        _logger.LogInformation("Player connected: {ClientId}", clientId);
        return Task.CompletedTask;
    }

    public Task OnDisconnectedAsync(string clientId, Exception? exception)
    {
        var world = _worlds.GetWorld(0);
        if (world != null)
        {
            var player = world.FindAllObjects<ArenaPlayer>().FirstOrDefault(p => p.ClientId == clientId);
            if (player != null)
                world.DestroyObject(player);
        }
        return Task.CompletedTask;
    }

    [Gate("join")]
    public async Task OnJoin(CJoin packet, string clientId)
    {
        var vid = (uint)Interlocked.Increment(ref _nextVid);
        var x = Random.Shared.Next(1000, 9000);
        var z = Random.Shared.Next(1000, 9000);

        var player = new ArenaPlayer(vid, packet.Name, x, 0, z, clientId);
        var world = _worlds.GetWorld(0);
        if (world != null)
            await world.SpawnDynamicObject(player);

        await _router.Client.SendAsync(clientId, new SJoinOk
        {
            Vid = vid, X = x, Y = 0, Z = z,
        });

        _logger.LogInformation("Player {Name} (vid={Vid}) spawned at ({X},0,{Z})", packet.Name, vid, x, z);
    }

    [Gate("move")]
    public Task OnMove(CMove packet, string clientId)
    {
        var world = _worlds.GetWorld(0);
        var player = world?.FindAllObjects<ArenaPlayer>().FirstOrDefault(p => p.ClientId == clientId);
        if (player == null) return Task.CompletedTask;

        // Update position — [Synced] auto-broadcasts to nearby players
        player.Transform = Transform3D.From(
            new Vector3(packet.X, packet.Y, packet.Z),
            Quaternion.Identity, Vector3.One);

        return Task.CompletedTask;
    }

    [Gate("attack")]
    public Task OnAttack(CAttack packet, string clientId)
    {
        var world = _worlds.GetWorld(0);
        var attacker = world?.FindAllObjects<ArenaPlayer>().FirstOrDefault(p => p.ClientId == clientId);
        if (attacker == null) return Task.CompletedTask;

        var target = world!.FindAllObjects<ArenaMonster>()
            .FirstOrDefault(m => m.VirtualId == packet.TargetVid);
        if (target == null) return Task.CompletedTask;

        // Single-target attack via CombatService — fires OnHit/OnDeath events
        _combat.Attack(attacker, target);
        return Task.CompletedTask;
    }

    [Gate("aoe")]
    public Task OnAoeAttack(CAoeAttack packet, string clientId)
    {
        var world = _worlds.GetWorld(0);
        var attacker = world?.FindAllObjects<ArenaPlayer>().FirstOrDefault(p => p.ClientId == clientId);
        if (attacker == null) return Task.CompletedTask;

        // Sphere sweep — hits all monsters within radius
        var query = SweepQuery.Sphere(attacker.PosX, attacker.PosY, attacker.PosZ, packet.Radius);
        _combat.Sweep(attacker, query);
        return Task.CompletedTask;
    }
}
