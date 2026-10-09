/*
Copyright 2025 Aron Gere
Licensed under the Apache License, Version 2.0
*/

using MessagePack;
using Microsoft.Extensions.Logging;

namespace Altruist.Gaming.Combat;

// ── Packets ──────────────────────────────────────────

/// <summary>
/// Client → server request handled by the <c>"attack"</c> gate of <see cref="AltruistCombatPortal"/>: attack the
/// entity with <see cref="TargetVID"/>. Implements <see cref="ILagCompensated"/>, so a non-zero
/// <see cref="ClientTick"/> makes the server validate the hit at the client's perceived tick.
/// </summary>
[MessagePackObject]
public class AttackPacket : IPacketBase, ILagCompensated
{
    /// <inheritdoc/>
    [Key(0)] public uint MessageCode { get; set; }
    /// <summary>Game-defined attack kind (e.g. basic/skill slot). Not read by the base portal.</summary>
    [Key(1)] public byte Type { get; set; }
    /// <summary>Virtual id of the target entity (resolved via <c>FindTarget</c>).</summary>
    [Key(2)] public uint TargetVID { get; set; }
    /// <summary>Engine tick the client saw when attacking; 0 = no lag compensation.</summary>
    [Key(3)] public long ClientTick { get; set; }
}

/// <summary>Client → server request handled by the <c>"target"</c> gate: select an entity and receive its
/// <see cref="TargetInfoPacket"/>.</summary>
[MessagePackObject]
public class TargetPacket : IPacketBase
{
    /// <inheritdoc/>
    [Key(0)] public uint MessageCode { get; set; }
    /// <summary>Virtual id of the entity being targeted.</summary>
    [Key(1)] public uint VID { get; set; }
}

/// <summary>Server → client broadcast of one hit, sent to nearby clients by <c>BroadcastHit</c>.</summary>
[MessagePackObject]
public class DamagePacket : IPacketBase
{
    /// <inheritdoc/>
    [Key(0)] public uint MessageCode { get; set; }
    /// <summary>Virtual id of the entity that was hit.</summary>
    [Key(1)] public uint VID { get; set; }
    /// <summary>Low 8 bits of the hit's <see cref="DamageFlags"/> (<see cref="DamageFlags.Killed"/> does not fit;
    /// a <see cref="DeathPacket"/> follows instead).</summary>
    [Key(2)] public byte Flags { get; set; }
    /// <summary>Damage applied.</summary>
    [Key(3)] public int Damage { get; set; }
}

/// <summary>Server → client broadcast that an entity died, sent after its <see cref="DamagePacket"/>.</summary>
[MessagePackObject]
public class DeathPacket : IPacketBase
{
    /// <inheritdoc/>
    [Key(0)] public uint MessageCode { get; set; }
    /// <summary>Virtual id of the entity that died.</summary>
    [Key(1)] public uint VID { get; set; }
}

/// <summary>Server → client reply to a <see cref="TargetPacket"/> with the target's health.</summary>
[MessagePackObject]
public class TargetInfoPacket : IPacketBase
{
    /// <inheritdoc/>
    [Key(0)] public uint MessageCode { get; set; }
    /// <summary>Virtual id of the targeted entity.</summary>
    [Key(1)] public uint VID { get; set; }
    /// <summary>Health as an integer percentage of max health (0-100, truncated; 0 when max health is 0).</summary>
    [Key(2)] public byte HPPercent { get; set; }
}

// ── Base Portal ──────────────────────────────────────

/// <summary>
/// Base combat portal with attack and target gates.
/// Extend this in your game — implement ResolveAttacker and FindTarget.
/// Override OnAttackCompleted/OnSweepCompleted for game-specific post-processing.
/// </summary>
/// <remarks>
/// <para>
/// Choosing: the portal is the network layer (packets in, broadcasts out) over <see cref="ICombatService"/>, which
/// holds the actual rules. Use this base when clients target and attack entities by virtual id (tab-target / MMO
/// style). For other input models (skill shots, physics hits) write your own <see cref="Portal"/> and call
/// <see cref="ICombatService"/> directly; for game reactions (XP, loot) prefer <see cref="CombatHandlerAttribute"/>
/// classes over portal overrides.
/// </para>
/// <para>
/// Gates: <c>"attack"</c> (<see cref="AttackPacket"/>) and <c>"target"</c> (<see cref="TargetPacket"/>). Gate event
/// names are global, so only one combat portal subclass should be active. There is no sweep gate: call
/// <see cref="ICombatService.Sweep"/> from your own gate, then <see cref="BroadcastSweep"/> and
/// <see cref="OnSweepCompleted"/> yourself (the base never calls them).
/// </para>
/// </remarks>
/// <example>
/// <code>
/// public sealed class MyCombatPortal(ICombatService combat, IAltruistRouter router, ILoggerFactory lf, IMyWorld world)
///     : AltruistCombatPortal(combat, router, lf)
/// {
///     protected override Task&lt;ICombatEntity?&gt; ResolveAttacker(string clientId) =&gt; Task.FromResult(world.PlayerOf(clientId));
///     protected override ICombatEntity? FindTarget(uint vid) =&gt; world.Find(vid);
///     protected override IEnumerable&lt;string&gt; GetNearbyClientIds(ICombatEntity c, float range) =&gt; world.ClientsNear(c, range);
/// }
/// </code>
/// </example>
public abstract class AltruistCombatPortal : Portal
{
    /// <summary>The combat rules service.</summary>
    protected readonly ICombatService Combat;
    /// <summary>Router used to send packets to clients.</summary>
    protected readonly IAltruistRouter Router;
    /// <summary>Logger named after the concrete portal type.</summary>
    protected readonly ILogger Logger;

    /// <summary>Called by the derived portal's constructor (portals are created by DI).</summary>
    /// <param name="combat">Combat service.</param>
    /// <param name="router">Client router.</param>
    /// <param name="loggerFactory">Logger factory.</param>
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
    /// <param name="center">Entity the broadcast is about (the attacker for <see cref="BroadcastHit"/>).</param>
    /// <param name="range">Broadcast radius in world units (the base passes 5000).</param>
    /// <returns>Client ids; enumerated once per packet sent.</returns>
    protected abstract IEnumerable<string> GetNearbyClientIds(ICombatEntity center, float range);

    /// <summary>
    /// <c>"attack"</c> gate: resolves attacker and target, ignores the request if either is missing or the target is
    /// dead, runs <see cref="ICombatService.Attack"/> (lag-compensated via <see cref="AttackPacket.ClientTick"/>),
    /// broadcasts the hit, then calls <see cref="OnAttackCompleted"/>. Performs no range or cooldown checks;
    /// override to add them.
    /// </summary>
    /// <param name="packet">The attack request.</param>
    /// <param name="clientId">Sending client.</param>
    [Gate("attack")]
    public virtual async Task OnAttack(AttackPacket packet, string clientId)
    {
        var attacker = await ResolveAttacker(clientId);
        var target = FindTarget(packet.TargetVID);
        if (attacker == null || target == null || target.IsDead) return;

        var result = Combat.Attack(attacker, target);
        await BroadcastHit(attacker, result);
        await OnAttackCompleted(attacker, target, result, clientId);
    }

    /// <summary><c>"target"</c> gate: replies to the sender with a <see cref="TargetInfoPacket"/> for the requested
    /// entity (no reply when it does not exist).</summary>
    /// <param name="packet">The target request.</param>
    /// <param name="clientId">Sending client.</param>
    [Gate("target")]
    public virtual async Task OnTarget(TargetPacket packet, string clientId)
    {
        var target = FindTarget(packet.VID);
        if (target == null) return;

        var hpPct = target.MaxHealth > 0 ? (byte)(target.Health * 100 / target.MaxHealth) : (byte)0;
        await Router.Client.SendAsync(clientId, new TargetInfoPacket { VID = packet.VID, HPPercent = hpPct });
    }

    /// <summary>Override for game-specific post-attack logic.</summary>
    /// <param name="attacker">Resolved attacker.</param>
    /// <param name="target">Attacked entity.</param>
    /// <param name="result">Outcome of the attack.</param>
    /// <param name="clientId">Client that sent the attack.</param>
    /// <returns>A task the gate awaits.</returns>
    protected virtual Task OnAttackCompleted(ICombatEntity attacker, ICombatEntity target, HitResult result, string clientId)
        => Task.CompletedTask;

    /// <summary>Override for game-specific post-sweep logic. Not invoked by the base class; call it from your own
    /// sweep gate.</summary>
    /// <param name="attacker">Entity that swept.</param>
    /// <param name="result">Sweep outcome.</param>
    /// <param name="clientId">Client that triggered the sweep.</param>
    /// <returns>A task.</returns>
    protected virtual Task OnSweepCompleted(ICombatEntity attacker, SweepResult result, string clientId)
        => Task.CompletedTask;

    /// <summary>Sends a <see cref="DamagePacket"/> (and a <see cref="DeathPacket"/> if the hit was lethal) to every
    /// client returned by <see cref="GetNearbyClientIds"/> within 5000 units of <paramref name="center"/>.</summary>
    /// <param name="center">Entity to broadcast around (usually the attacker).</param>
    /// <param name="hit">Hit to announce.</param>
    /// <returns>A task completing when all sends finished (sequential).</returns>
    protected async Task BroadcastHit(ICombatEntity center, HitResult hit)
    {
        var packet = new DamagePacket
        {
            VID = hit.Target.VirtualId,
            Flags = (byte)hit.Flags,
            Damage = hit.Damage,
        };

        foreach (var cid in GetNearbyClientIds(center, 5000f))
            await Router.Client.SendAsync(cid, packet);

        if (hit.Killed)
        {
            var deathPacket = new DeathPacket { VID = hit.Target.VirtualId };
            foreach (var cid in GetNearbyClientIds(center, 5000f))
                await Router.Client.SendAsync(cid, deathPacket);
        }
    }

    /// <summary>Calls <see cref="BroadcastHit"/> for every hit of a sweep, all centered on <paramref name="center"/>.</summary>
    /// <param name="center">Entity to broadcast around (usually the attacker).</param>
    /// <param name="sweep">Sweep result from <see cref="ICombatService.Sweep"/>.</param>
    /// <returns>A task completing when all sends finished.</returns>
    protected async Task BroadcastSweep(ICombatEntity center, SweepResult sweep)
    {
        foreach (var hit in sweep.Hits)
            await BroadcastHit(center, hit);
    }
}
