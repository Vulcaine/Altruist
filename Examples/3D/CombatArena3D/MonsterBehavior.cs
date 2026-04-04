using System.Numerics;
using Altruist.Gaming;
using Altruist.ThreeD.Numerics;

namespace CombatArena3D;

public class MonsterAIContext : IAIContext
{
    public ITypelessWorldObject Entity { get; set; } = null!;
    public float TimeInState { get; set; }
    public ArenaMonster Monster { get; set; } = null!;
    public ArenaPlayer? Target { get; set; }
    public float SpawnX { get; set; }
    public float SpawnY { get; set; }
    public float SpawnZ { get; set; }
}

/// <summary>
/// Monster AI: Idle (wait for target), Chase (pursue player), Attack (damage at range), Return (go back to spawn).
/// </summary>
[AIBehavior("arena_monster")]
public class ArenaMonsterBehavior
{
    private const float ChaseSpeed = 120f;
    private const float AttackRange = 200f;
    private const float LeashRange = 3000f;

    [AIState("Idle", Initial = true)]
    public string? Idle(MonsterAIContext ctx, float dt)
    {
        if (ctx.Target != null && !ctx.Target.IsDead)
            return "Chase";
        return null;
    }

    [AIState("Chase")]
    public string? Chase(MonsterAIContext ctx, float dt)
    {
        if (ctx.Target == null || ctx.Target.IsDead)
            return "Return";

        var m = ctx.Monster;
        var pos = new Vector3(m.Transform.Position.X, m.Transform.Position.Y, m.Transform.Position.Z);
        var tpos = new Vector3(ctx.Target.Transform.Position.X, ctx.Target.Transform.Position.Y, ctx.Target.Transform.Position.Z);
        var dir = tpos - pos;
        var dist = dir.Length();

        // Leash check
        var spawn = new Vector3(ctx.SpawnX, ctx.SpawnY, ctx.SpawnZ);
        if (Vector3.Distance(pos, spawn) > LeashRange)
            return "Return";

        if (dist <= AttackRange)
            return "Attack";

        // Move toward target
        if (dist > 1f)
        {
            var move = Vector3.Normalize(dir) * ChaseSpeed * dt;
            m.Transform = Transform3D.From(pos + move, Quaternion.Identity, Vector3.One);
        }

        return null;
    }

    [AIState("Attack", Delay = 1.5f)]
    public string? Attack(MonsterAIContext ctx, float dt)
    {
        if (ctx.Target == null || ctx.Target.IsDead)
            return "Return";

        // After delay, check if still in range
        var mPos = new Vector3(ctx.Monster.Transform.Position.X, ctx.Monster.Transform.Position.Y, ctx.Monster.Transform.Position.Z);
        var tPos = new Vector3(ctx.Target.Transform.Position.X, ctx.Target.Transform.Position.Y, ctx.Target.Transform.Position.Z);
        var dist = Vector3.Distance(mPos, tPos);
        if (dist > AttackRange * 1.5f)
            return "Chase";

        // Deal damage (in a real game, use ICombatService)
        ctx.Target.Hp = Math.Max(0, ctx.Target.Hp - ctx.Monster.GetAttackPower());
        if (ctx.Target.Hp <= 0)
            ctx.Target.IsDead = true;

        return "Chase"; // Continue pursuit
    }

    [AIState("Return")]
    public string? Return(MonsterAIContext ctx, float dt)
    {
        var m = ctx.Monster;
        var pos = new Vector3(m.Transform.Position.X, m.Transform.Position.Y, m.Transform.Position.Z);
        var spawn = new Vector3(ctx.SpawnX, ctx.SpawnY, ctx.SpawnZ);
        var dist = Vector3.Distance(pos, spawn);

        if (dist < 10f)
            return "Idle";

        var dir = Vector3.Normalize(spawn - pos);
        m.Transform = Transform3D.From(pos + dir * ChaseSpeed * dt, Quaternion.Identity, Vector3.One);
        return null;
    }

    [AIStateEnter("Return")]
    public void ReturnEnter(MonsterAIContext ctx) => ctx.Target = null;
}
