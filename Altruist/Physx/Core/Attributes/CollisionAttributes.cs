namespace Altruist.Physx
{
    /// <summary>
    /// Marks a class as containing collision event handlers.
    /// Classes with this attribute are discovered via reflection.
    /// </summary>
    [AttributeUsage(AttributeTargets.Class, AllowMultiple = false)]
    public sealed class CollisionHandlerAttribute : Attribute
    {
    }

    /// <summary>
    /// Marks a method as a collision event handler.
    /// The EventType is an arbitrary type you can use to represent
    /// the logical "event" you want to raise/send (e.g. network DTO).
    ///
    /// The *parameters* of the method define which entity/component
    /// types this handler cares about, e.g.:
    ///
    ///   [CollisionEvent(typeof(PlayerHitTreeEvent))]
    ///   void OnHit(Player player, Tree tree) { ... }
    /// </summary>
    [AttributeUsage(AttributeTargets.Method, AllowMultiple = true)]
    public sealed class CollisionEventAttribute : Attribute
    {
        public Type EventType { get; }

        public CollisionEventAttribute(Type eventType)
        {
            EventType = eventType ?? throw new ArgumentNullException(nameof(eventType));
        }
    }

    // ── Built-in collision event phases ──────────────────────────────

    /// <summary>First frame two entities begin overlapping.</summary>
    public class CollisionEnter { }

    /// <summary>Every tick while two entities remain overlapping.</summary>
    public class CollisionStay { }

    /// <summary>First frame after two entities stop overlapping.</summary>
    public class CollisionExit { }

    /// <summary>One-shot hit (combat damage, projectile impact). No tracking.</summary>
    public class CollisionHit { }

    // ── Visibility events (bridged from VisibilityTracker) ───────────

    /// <summary>Entity entered an observer's view range.</summary>
    public class EntityVisible { }

    /// <summary>Entity left an observer's view range.</summary>
    public class EntityInvisible { }
}
