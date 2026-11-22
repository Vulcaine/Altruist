namespace Altruist.Physx.Contracts
{

    /// <summary>
    /// Engine-agnostic collider (shape) attached to an <see cref="IPhysxBody"/>. Dimension-specific variants
    /// (e.g. <c>IPhysxCollider3D</c>, <c>IPhysxCollider2D</c>) add shape, size and transform data.
    /// </summary>
    public interface IPhysxCollider
    {
        /// <summary>Unique collider identifier.</summary>
        string Id { get; }
        /// <summary>
        /// When <see langword="true"/> the collider only reports overlaps (trigger events) and does not produce a
        /// solid contact response.
        /// </summary>
        bool IsTrigger { get; set; }
        /// <summary>Arbitrary application object associated with this collider (e.g. the owning entity).</summary>
        object? UserData { get; set; }
        /// <summary>Raised when another collider starts overlapping this trigger. Arguments: (this collider, other collider).</summary>
        event Action<IPhysxCollider, IPhysxCollider>? OnTriggerEnter;
        /// <summary>Raised when another collider stops overlapping this trigger. Arguments: (this collider, other collider).</summary>
        event Action<IPhysxCollider, IPhysxCollider>? OnTriggerExit;
    }



}
