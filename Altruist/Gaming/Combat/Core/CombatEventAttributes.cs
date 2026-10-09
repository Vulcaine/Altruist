namespace Altruist.Gaming.Combat;

/// <summary>
/// Marks a class as a combat event handler: a singleton whose <see cref="CombatEventAttribute"/> methods are
/// invoked by <see cref="ICombatEventDispatcher"/> whenever <see cref="ICombatService"/> raises a hit, sweep or death.
/// </summary>
/// <remarks>
/// <para>
/// Discovery is automatic: <see cref="CombatEventHandlerConfig"/> registers every <c>[CombatHandler]</c> class in the
/// loaded assemblies as a DI singleton (constructor injection works as for any service, including config-conditional
/// registration), and <see cref="CombatHandlerInitializer"/> wires its methods after the container is built.
/// </para>
/// <para>
/// Choosing between the three ways to react to combat:
/// <list type="bullet">
/// <item><c>[CombatHandler]</c> + <see cref="CombatEventAttribute"/>: declarative, typed per actor pair
/// (e.g. only fire when a <c>Player</c> hits a <c>Monster</c>); best for game rules such as XP, loot, aggro.</item>
/// <item>The C# events <see cref="ICombatService.OnHit"/>, <see cref="ICombatService.OnDeath"/>,
/// <see cref="ICombatService.OnSweep"/>: one callback for every event regardless of actor types; best for
/// cross-cutting concerns (metrics, logging) or code that already holds the service.</item>
/// <item>Overriding <c>OnAttackCompleted</c> on an <see cref="AltruistCombatPortal"/>: per-request network
/// post-processing (replying to the attacking client).</item>
/// </list>
/// </para>
/// </remarks>
/// <example>
/// <code>
/// [CombatHandler]
/// public sealed class XpRules(IXpService xp)
/// {
///     [CombatEvent(typeof(DeathEvent))]
///     public void OnKill(DeathEvent e, Monster victim, Player killer) =&gt; xp.Grant(killer, victim.XpReward);
///
///     [CombatEvent(typeof(HitEvent))]
///     public void OnAnyHitByPlayer(HitEvent e, Player attacker) { /* single-actor form */ }
/// }
/// </code>
/// </example>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false)]
public sealed class CombatHandlerAttribute : Attribute
{
}

/// <summary>
/// Marks an instance method of a <see cref="CombatHandlerAttribute"/> class as a handler for one combat event type.
/// </summary>
/// <remarks>
/// <para>
/// Required signature (validated at startup, violations throw <see cref="InvalidOperationException"/>):
/// <c>void Method(TPayload payload, TA a)</c> or <c>void Method(TPayload payload, TA a, TB b)</c>, where
/// <c>TPayload</c> implements <see cref="ICombatEventPayload"/> and is assignable to/from <see cref="EventType"/>,
/// and <c>TA</c>/<c>TB</c> are concrete (non-abstract, non-interface) classes. Return type must be <c>void</c>.
/// </para>
/// <para>
/// Matching is by EXACT runtime type: the dispatcher looks handlers up by <c>payload.GetType()</c> and the actors'
/// <c>GetType()</c>, so a handler declared for a base class does not fire for a subclass, and <see cref="EventType"/>
/// should be the concrete payload type raised (<see cref="HitEvent"/>, <see cref="SweepEvent"/>, <see cref="DeathEvent"/>).
/// Single-actor handlers match the dispatch's primary actor only; two-actor handlers match the (primary, secondary)
/// pair in either order (arguments are swapped to fit the declared parameter order).
/// Actor order raised by <see cref="CombatService"/>: hit = (attacker, target); sweep = (attacker) only;
/// death = (victim, killer) or (victim) when there is no killer.
/// </para>
/// <para>Handlers run synchronously on the thread that called the combat service (normally the tick/gate thread);
/// exceptions are caught and logged so one failing handler does not stop the others.</para>
/// </remarks>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = true)]
public sealed class CombatEventAttribute : Attribute
{
    /// <summary>The payload type this method handles (e.g. <c>typeof(HitEvent)</c>).</summary>
    public Type EventType { get; }

    /// <summary>Declares the method as a handler for <paramref name="eventType"/>.</summary>
    /// <param name="eventType">Concrete <see cref="ICombatEventPayload"/> type, e.g. <c>typeof(DeathEvent)</c>.</param>
    /// <exception cref="ArgumentNullException"><paramref name="eventType"/> is null.</exception>
    public CombatEventAttribute(Type eventType)
    {
        EventType = eventType ?? throw new ArgumentNullException(nameof(eventType));
    }
}

/// <summary>
/// Marker for payloads that can be routed through <see cref="ICombatEventDispatcher"/> to
/// <see cref="CombatEventAttribute"/> handlers. Built-in payloads: <see cref="HitEvent"/>, <see cref="SweepEvent"/>,
/// <see cref="DeathEvent"/>; implement it on your own types to dispatch custom combat events (e.g. heal, stun).
/// </summary>
public interface ICombatEventPayload
{
}
