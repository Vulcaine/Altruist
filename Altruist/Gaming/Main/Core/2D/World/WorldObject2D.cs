/*
Copyright 2025 Aron Gere
Licensed under the Apache License, Version 2.0
*/

using Altruist.Persistence;
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
    public interface IWorldObject2D : IWorldObject<IGameWorldManager2D>
    {
        /// <summary>Client connection ID for player-linked objects.</summary>
        [VaultIgnore]
        string ClientId { get; set; }

        /// <summary>Transform in world space.</summary>
        [VaultIgnore]
        Transform2D Transform { get; set; }

        /// <summary>
        /// Optional physics body backing this object.
        /// Static world geometry might be static bodies; dynamic objects might swap bodies.
        /// </summary>
        [VaultIgnore]
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
        [VaultIgnore]
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

        [VaultIgnore]
        public Transform2D Transform { get; set; }

        [VaultIgnore]
        public string ZoneId { get; set; } = string.Empty;

        [VaultIgnore]
        public IPhysxBody2D? Body { get; set; }

        [VaultIgnore]
        public bool Expired { get; set; }

        [VaultIgnore]
        public string ClientId { get; set; } = "";

        protected WorldObject2D(Transform2D transform, string zoneId = "", string? archetype = null)
        {
            Transform = transform;
            ZoneId = zoneId ?? string.Empty;
            ObjectArchetype = archetype;
        }

        public virtual void Step(float dt, IGameWorldManager2D world)
        {
            return;
        }
    }

    /// <summary>
    /// Base for 2D prefabs that also behave as world object descriptors.
    /// - Inherits PrefabModel for persistence
    /// - Implements IWorldObject2D for runtime/world indexing
    /// </summary>
    public abstract class WorldObjectPrefab2D : PrefabModel, IWorldObject2D
    {
        [VaultIgnore]
        public string ClientId { get; set; } = "";

        [VaultIgnore]
        public string InstanceId { get; set; } = Guid.NewGuid().ToString();

        [VaultColumn("archetype")]
        public string? ObjectArchetype { get; set; } = null;

        [VaultIgnore]
        public Transform2D Transform { get; set; }

        [VaultIgnore]
        public IPhysxBody2D? Body { get; set; }

        [VaultIgnore]
        public string ZoneId { get; set; } = "";

        [VaultIgnore]
        public bool Expired { get; set; }

        public virtual void Step(float dt, IGameWorldManager2D world) { }
    }

    /// <summary>
    /// Concrete world object created dynamically at runtime (not loaded from a schema).
    /// </summary>
    public class AnonymousWorldObject2D : WorldObject2D
    {
        public AnonymousWorldObject2D(Transform2D transform, string zoneId = "", string? archetype = null)
            : base(transform, zoneId: zoneId, archetype: archetype)
        {
        }
    }
}
