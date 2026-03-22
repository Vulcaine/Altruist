/*
Copyright 2025 Aron Gere
Licensed under the Apache License, Version 2.0
*/

using MessagePack;
using Microsoft.Extensions.Logging;

namespace Altruist.Gaming.Combat;

// ── Packets ──────────────────────────────────────────

[MessagePackObject]
public class CAttackPacket : IPacketBase
{
    [Key(0)] public uint MessageCode { get; set; }
    [Key(1)] public byte Type { get; set; }
    [Key(2)] public uint TargetVID { get; set; }
}

[MessagePackObject]
public class CTargetPacket : IPacketBase
{
    [Key(0)] public uint MessageCode { get; set; }
    [Key(1)] public uint VID { get; set; }
}

[MessagePackObject]
public class SDamagePacket : IPacketBase
{
    [Key(0)] public uint MessageCode { get; set; }
    [Key(1)] public uint VID { get; set; }
    [Key(2)] public byte Flags { get; set; }
    [Key(3)] public int Damage { get; set; }
}

[MessagePackObject]
public class SDeathPacket : IPacketBase
{
    [Key(0)] public uint MessageCode { get; set; }
    [Key(1)] public uint VID { get; set; }
}

[MessagePackObject]
public class STargetPacket : IPacketBase
{
    [Key(0)] public uint MessageCode { get; set; }
    [Key(1)] public uint VID { get; set; }
    [Key(2)] public byte HPPercent { get; set; }
}

// ── Base Portal ──────────────────────────────────────

/// <summary>
/// Base combat portal with attack and target gates.
/// Extend this in your game — implement ResolveAttacker and FindTarget.
/// Override OnAttackCompleted/OnSweepCompleted for game-specific post-processing.
/// </summary>
public abstract class AltruistCombatPortal : Portal
{
    protected readonly ICombatService Combat;
    protected readonly IAltruistRouter Router;
    protected readonly ILogger Logger;

    protected AltruistCombatPortal(
        ICombatService combat,
        IAltruistRouter router,
        ILoggerFactory loggerFactory)
    {
        Combat = combat;
        Router = router;
        Logger = loggerFactory.CreateLogger(GetType());
    }

    /// <summary>Resolve the attacking entity from a client connection ID.</summary>
    protected abstract Task<ICombatEntity?> ResolveAttacker(string clientId);

    /// <summary>Find a combat entity by its virtual ID.</summary>
    protected abstract ICombatEntity? FindTarget(uint vid);

    /// <summary>Get all client IDs that should receive combat broadcasts near an entity.</summary>
    protected abstract IEnumerable<string> GetNearbyClientIds(ICombatEntity center, float range);

    [Gate("attack")]
    public virtual async Task OnAttack(CAttackPacket packet, string clientId)
    {
        var attacker = await ResolveAttacker(clientId);
        var target = FindTarget(packet.TargetVID);
        if (attacker == null || target == null || target.IsDead) return;

        var result = Combat.Attack(attacker, target);
        await BroadcastHit(attacker, result);
        await OnAttackCompleted(attacker, target, result, clientId);
    }

    [Gate("target")]
    public virtual async Task OnTarget(CTargetPacket packet, string clientId)
    {
        var target = FindTarget(packet.VID);
        if (target == null) return;

        var hpPct = target.MaxHealth > 0 ? (byte)(target.Health * 100 / target.MaxHealth) : (byte)0;
        await Router.Client.SendAsync(clientId, new STargetPacket { VID = packet.VID, HPPercent = hpPct });
    }

    /// <summary>Override for game-specific post-attack logic.</summary>
    protected virtual Task OnAttackCompleted(ICombatEntity attacker, ICombatEntity target, HitResult result, string clientId)
        => Task.CompletedTask;

    /// <summary>Override for game-specific post-sweep logic.</summary>
    protected virtual Task OnSweepCompleted(ICombatEntity attacker, SweepResult result, string clientId)
        => Task.CompletedTask;

    protected async Task BroadcastHit(ICombatEntity center, HitResult hit)
    {
        var packet = new SDamagePacket
        {
            VID = hit.Target.VirtualId,
            Flags = (byte)hit.Flags,
            Damage = hit.Damage,
        };

        foreach (var cid in GetNearbyClientIds(center, 5000f))
            await Router.Client.SendAsync(cid, packet);

        if (hit.Killed)
        {
            var deathPacket = new SDeathPacket { VID = hit.Target.VirtualId };
            foreach (var cid in GetNearbyClientIds(center, 5000f))
                await Router.Client.SendAsync(cid, deathPacket);
        }
    }

    protected async Task BroadcastSweep(ICombatEntity center, SweepResult sweep)
    {
        foreach (var hit in sweep.Hits)
            await BroadcastHit(center, hit);
    }
}
