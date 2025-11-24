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
}
