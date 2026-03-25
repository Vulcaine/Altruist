using Altruist;
using Altruist.Gaming.Combat;
using Altruist.Gaming.TwoD;
using Microsoft.Extensions.Logging;

namespace ShooterGame2D;

[Portal("/game")]
public class ShooterPortal : Portal, OnConnectedAsync, OnDisconnectedAsync
{
    private readonly IAltruistRouter _router;
    private readonly IGameWorldOrganizer2D _worlds;
    private readonly ICombatService _combat;
    private readonly ILogger _logger;
    private static uint _nextVid = 1;

    public ShooterPortal(
        IAltruistRouter router,
        IGameWorldOrganizer2D worlds,
        ICombatService combat,
        ILoggerFactory loggerFactory)
    {
        _router = router;
        _worlds = worlds;
        _combat = combat;
        _logger = loggerFactory.CreateLogger<ShooterPortal>();
    }

    public async Task OnConnectedAsync(string clientId, ConnectionManager manager, AltruistConnection connection)
    {
        _logger.LogInformation("Player connected: {ClientId}", clientId);
        await Task.CompletedTask;
    }

    public Task OnDisconnectedAsync(string clientId, Exception? exception)
    {
        // Remove player's world object
        var world = _worlds.GetWorld(0);
        if (world != null)
        {
            var obj = world.FindAllObjects<PlayerShip>().FirstOrDefault(p => p.ClientId == clientId);
            if (obj != null)
                world.DestroyObject(obj);
        }
        return Task.CompletedTask;
    }

    [Gate("join")]
    public async Task OnJoin(CJoin packet, string clientId)
    {
        var vid = Interlocked.Increment(ref _nextVid);
        var x = Random.Shared.Next(100, 1900);
        var y = Random.Shared.Next(100, 1900);

        var player = new PlayerShip((uint)vid, packet.Name, x, y, clientId);
        var world = _worlds.GetWorld(0);
        if (world != null)
            await world.SpawnDynamicObject(player);

        await _router.Client.SendAsync(clientId, new SJoinOk
        {
            Vid = (uint)vid, X = x, Y = y,
        });

        _logger.LogInformation("Player {Name} spawned at ({X},{Y})", packet.Name, x, y);
    }

    [Gate("shoot")]
    public async Task OnShoot(CShoot packet, string clientId)
    {
        var world = _worlds.GetWorld(0);
        if (world == null) return;

        var attacker = world.FindAllObjects<PlayerShip>().FirstOrDefault(p => p.ClientId == clientId);
        if (attacker == null) return;

        // Find target by VID
        ICombatEntity? target = world.FindAllObjects<EnemyDrone>()
            .FirstOrDefault(d => d.VirtualId == packet.TargetVid);
        target ??= world.FindAllObjects<PlayerShip>()
            .FirstOrDefault(p => p.VirtualId == packet.TargetVid);

        if (target == null) return;

        var result = _combat.Attack(attacker, target);

        await _router.Broadcast.SendAsync(new SHit
        {
            TargetVid = packet.TargetVid,
            Damage = result.Damage,
            RemainingHp = target.Health,
        });

        if (result.Killed)
        {
            await _router.Broadcast.SendAsync(new SDeath { Vid = packet.TargetVid });
        }
    }
}
