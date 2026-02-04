/*
Copyright 2025 Aron Gere
Licensed under the Apache License, Version 2.0
*/

using Altruist.Physx.TwoD;
using Altruist.TwoD.Numerics;

namespace Altruist.Gaming.TwoD
{
    using Altruist.Gaming;
    using Altruist.UORM;

    /// <summary>
    /// Contract for a 2D world entity that can live in partitions
    /// and be associated with a physics body.
    /// </summary>
    public interface IWorldObject2D : IWorldObject
    {
        /// <summary>Transform in world space.</summary>
        Transform2D Transform { get; set; }

        /// <summary>
        /// Optional physics body backing this object.
        /// Static world geometry might be static bodies; dynamic objects might swap bodies.
        /// </summary>
        IPhysxBody2D? Body { get; set; }
    }

    /// <summary>
    /// Convenience base implementation wired for typical usage:
    /// - auto InstanceId
    /// - Archetype resolved from [WorldObject] attribute by default
    /// - Transform + Body stored as properties
    /// </summary>
    public abstract class WorldObject2D : IWorldObject2D
    {
        public string InstanceId { get; set; } = Guid.NewGuid().ToString("N");

        /// <summary>
        /// By default resolves the archetype from [WorldObject] on the concrete type.
        /// Override if you need something custom.
        /// </summary>
        public virtual string? ObjectArchetype
        {
            get;
            set;
        }

        public Transform2D Transform { get; set; }
        public string ZoneId { get; set; } = string.Empty;

        public IPhysxBody2D? Body { get; set; }

        [VaultIgnore]
        public bool Expired { get; set; }

        protected WorldObject2D(Transform2D transform, string zoneId = "", string archetype = "")
        {
            Transform = transform;
            ZoneId = zoneId ?? string.Empty;
            ObjectArchetype = archetype;
        }

        public virtual void Step(float dt)
        {
            return;
        }
    }
}
