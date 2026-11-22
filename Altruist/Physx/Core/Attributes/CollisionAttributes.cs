namespace Altruist.Physx
{
    /// <summary>
    /// Marks a class as containing collision event handlers (methods tagged with
    /// <see cref="CollisionEventAttribute"/>).
    /// </summary>
    /// <remarks>
    /// Discovery is reflection-based: at startup <see cref="AltruistCollisionHandlerConfig"/> scans every
    /// loaded, non-dynamic assembly for non-abstract classes carrying this attribute (not inherited),
    /// registers each as a DI <b>singleton</b> (constructor dependencies are autowired, and
    /// <c>[ConditionalOnConfig]</c> gating is honoured), then resolves the instances from the root provider
    /// and registers their <see cref="CollisionEventAttribute"/> methods into <see cref="CollisionHandlerRegistry"/>.
    /// Without DI, call <see cref="CollisionHandlerDiscovery.RegisterCollisionHandlers"/> yourself.
    /// </remarks>
    /// <example>
    /// <code>
    /// [CollisionHandler]
    /// public sealed class PickupHandlers
    /// {
    ///     private readonly IScoreService _scores;              // autowired from DI
    ///     public PickupHandlers(IScoreService scores) =&gt; _scores = scores;
    ///
    ///     [CollisionEvent(typeof(CollisionEnter))]
    ///     private void OnEnter(CollisionEnter e, PlayerEntity player, CoinEntity coin) =&gt; _scores.Add(player, 1);
    /// }
    /// </code>
    /// </example>
    [AttributeUsage(AttributeTargets.Class, AllowMultiple = false)]
    public sealed class CollisionHandlerAttribute : Attribute
    {
    }

    /// <summary>
    /// Marks an instance method (public or non-public) of a <see cref="CollisionHandlerAttribute"/> class as a
    /// collision event handler. The <see cref="EventType"/> is the logical event key the dispatcher filters on
    /// (a built-in phase such as <see cref="CollisionEnter"/>/<see cref="CollisionStay"/>/<see cref="CollisionExit"/>/<see cref="CollisionHit"/>,
    /// or any custom class, e.g. a network DTO).
    /// </summary>
    /// <remarks>
    /// The method signature is validated at registration and must be exactly
    /// <c>void M(TPayload payload, TA a, TB b)</c>:
    /// <list type="bullet">
    /// <item><c>TPayload</c> is a concrete (non-abstract) class assignable to or from <see cref="EventType"/>.</item>
    /// <item><c>TA</c> and <c>TB</c> are concrete (non-abstract) classes; they select which entity pair the handler
    /// receives. A handler fires for any pair whose runtime types are assignable to <c>TA</c> and <c>TB</c> (so a handler
    /// for a base class also receives derived types), in either order: a handler for <c>(TA, TB)</c> also fires for a
    /// <c>(TB, TA)</c> contact (the dispatcher reorders arguments).</item>
    /// <item>The return type is <see langword="void"/>.</item>
    /// </list>
    /// Violations throw <see cref="InvalidOperationException"/> during startup. The attribute may be applied
    /// several times to subscribe one method to several event types.
    /// </remarks>
    /// <example>
    /// <code>
    /// [CollisionEvent(typeof(PlayerHitTreeEvent))]
    /// void OnHit(PlayerHitTreeEvent payload, Player player, Tree tree) { ... }
    /// </code>
    /// </example>
    [AttributeUsage(AttributeTargets.Method, AllowMultiple = true)]
    public sealed class CollisionEventAttribute : Attribute
    {
        /// <summary>Event type this handler subscribes to; the dispatcher only invokes the handler for events of exactly this type.</summary>
        public Type EventType { get; }

        /// <summary>Subscribes the decorated method to <paramref name="eventType"/>.</summary>
        /// <param name="eventType">Event key type (e.g. <c>typeof(CollisionEnter)</c>).</param>
        /// <exception cref="ArgumentNullException"><paramref name="eventType"/> is <see langword="null"/>.</exception>
        public CollisionEventAttribute(Type eventType)
        {
            EventType = eventType ?? throw new ArgumentNullException(nameof(eventType));
        }
    }

    // ── Built-in collision event phases ──────────────────────────────

    /// <summary>Event key/payload raised on the first tick two entities begin overlapping. Carries no data.</summary>
    public class CollisionEnter { }

    /// <summary>Event key/payload raised every tick while two entities remain overlapping (after <see cref="CollisionEnter"/>). Carries no data.</summary>
    public class CollisionStay { }

    /// <summary>Event key/payload raised on the first tick after two entities stop overlapping. Carries no data.</summary>
    public class CollisionExit { }

    /// <summary>
    /// One-shot hit event (damage, projectile impact). Unlike <see cref="CollisionEnter"/>/<see cref="CollisionStay"/>/<see cref="CollisionExit"/>
    /// no overlap state is tracked; the payload carries the hit details.
    /// </summary>
    public class CollisionHit
    {
        /// <summary>Object that caused the hit (e.g. the attacker or projectile owner), if any.</summary>
        public object? Source { get; init; }
        /// <summary>Object that received the hit, if any.</summary>
        public object? Target { get; init; }
        /// <summary>Damage amount; units and meaning are defined by the application.</summary>
        public int Damage { get; init; }
        /// <summary>Application-defined bit flags (e.g. critical, blocked).</summary>
        public uint Flags { get; init; }
        /// <summary>Optional application-defined extra context.</summary>
        public object? Context { get; init; }
    }

    // ── Visibility events (bridged from VisibilityTracker) ───────────

    /// <summary>Event key raised when an entity enters an observer's view range (bridged from the visibility tracker). Carries no data.</summary>
    public class EntityVisible { }

    /// <summary>Event key raised when an entity leaves an observer's view range (bridged from the visibility tracker). Carries no data.</summary>
    public class EntityInvisible { }
}
