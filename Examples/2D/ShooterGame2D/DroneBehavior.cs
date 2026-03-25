using Altruist.Gaming;
using Altruist.TwoD.Numerics;

namespace ShooterGame2D;

/// <summary>
/// AI context for enemy drones — holds chase target and timers.
/// </summary>
public class DroneAIContext : IAIContext
{
    public ITypelessWorldObject Entity { get; set; } = null!;
    public float TimeInState { get; set; }
    public EnemyDrone Drone { get; set; } = null!;
    public PlayerShip? Target { get; set; }
    public float AttackCooldown { get; set; }
}

/// <summary>
/// [AIBehavior] for enemy drones. Two states: Idle (scan for players) and Chase (pursue + attack).
/// Discovered automatically at startup. One shared instance, per-entity FSM context.
/// </summary>
[AIBehavior("chase_drone")]
public class ChaseDroneBehavior
{
    [AIState("Idle", Initial = true)]
    public string? Idle(DroneAIContext ctx, float dt)
    {
        // Scan for a target every tick — in a real game you'd use spatial queries
        if (ctx.Target != null && !ctx.Target.IsDead)
            return "Chase";
        return null;
    }

    [AIState("Chase")]
    public string? Chase(DroneAIContext ctx, float dt)
    {
        if (ctx.Target == null || ctx.Target.IsDead)
            return "Idle";

        // Move toward target
        var drone = ctx.Drone;
        var tx = ctx.Target.Transform.Position.X;
        var ty = ctx.Target.Transform.Position.Y;
        var dx = tx - drone.Transform.Position.X;
        var dy = ty - drone.Transform.Position.Y;
        var dist = MathF.Sqrt(dx * dx + dy * dy);

        if (dist > 5)
        {
            var speed = 80f * dt;
            var nx = (int)(drone.Transform.Position.X + dx / dist * speed);
            var ny = (int)(drone.Transform.Position.Y + dy / dist * speed);
            drone.Transform = new Transform2D(
                Position2D.Of(nx, ny),
                drone.Transform.Size,
                drone.Transform.Scale,
                drone.Transform.Rotation);
        }

        return null;
    }

    [AIStateEnter("Idle")]
    public void IdleEnter(DroneAIContext ctx) => ctx.Target = null;
}
