/*
Copyright 2025 Aron Gere
Licensed under the Apache License, Version 2.0
*/

using Altruist.Persistence;
using Altruist.Physx.ThreeD;
using Altruist.ThreeD.Numerics;
using Altruist.UORM;

namespace Altruist.Gaming.ThreeD
{
    /// <summary>
    /// Base for 3D prefabs that also behave as world object descriptors.
    /// - Inherits Prefab3D for persistence
    /// - Implements IWorldObject3D for runtime/world indexing
    /// - Holds a non-persisted BodyDescriptor used by SpawnService3D
    /// </summary>
    public abstract class WorldObjectPrefab3D : PrefabModel, IWorldObject3D
    {
        [VaultIgnore]
        public string ClientId { get; set; } = "";

        // IWorldObject: InstanceId, RoomId come from Prefab3D already.
        // Prefab3D has:
        //   public virtual string InstanceId { get; set; }
        //   public virtual string RoomId { get; set; }
        [VaultIgnore]
        public string InstanceId { get; set; } = Guid.NewGuid().ToString();

        /// <summary>
        /// Archetype is resolved from [WorldObject] attribute by default.
        /// Override only if you need something special.
        /// </summary>
        [VaultColumn("archetype")]
        public string? ObjectArchetype { get; set; } = null;

        /// <summary>
        /// Transform in world space. Prefab3D already has this property.
        /// </summary>
        [VaultIgnore]
        public Transform3D Transform { get; set; }

        /// <summary>
        /// Engine-agnostic body descriptor (not persisted).
        /// This is used by SpawnService3D to create the runtime body.
        /// </summary>
        [VaultIgnore]
        public PhysxBody3DDesc? BodyDescriptor { get; set; }

        /// <summary>
        /// Engine-agnostic collider descriptor (not persisted).
        /// This is used by SpawnService3D to create the runtime body.
        /// </summary>
        [VaultIgnore]
        public IEnumerable<PhysxCollider3DDesc> ColliderDescriptors { get; set; } = Enumerable.Empty<PhysxCollider3DDesc>();

        [VaultIgnore]
        public IPhysxBody3D? Body { get; set; }

        [VaultIgnore]
        public IEnumerable<IPhysxCollider3D> Colliders { get; set; } = Enumerable.Empty<IPhysxCollider3D>();

        [VaultIgnore]
        public string ZoneId { get; set; } = "";

        [VaultIgnore]
        public bool Expired { get; set; }

        public virtual Task StepAsync(float dt, IWorldPhysics3D worldPhysics) { return Task.CompletedTask; }
    }

    /// <summary>
    /// Contract for a 3D world entity that can live in partitions
    /// and be associated with a physics body descriptor.
    /// </summary>
    public interface IWorldObject3D : IWorldObject
    {
        [VaultIgnore]
        public string ClientId { get; set; }
        /// <summary>Transform in world space.</summary>
        [VaultIgnore]
        Transform3D Transform { get; set; }

        /// <summary>
        /// Optional physics body descriptor backing this object.
        /// Static world geometry might be static bodies; dynamic objects might swap bodies.
        /// The descriptor is engine/provider agnostic.
        /// </summary>
        [VaultIgnore]
        PhysxBody3DDesc? BodyDescriptor { get; set; }

        [VaultIgnore]
        IEnumerable<PhysxCollider3DDesc> ColliderDescriptors { get; set; }

        IEnumerable<IPhysxCollider3D> Colliders { get; set; }

        [VaultIgnore]
        public IPhysxBody3D? Body { get; set; }
    }

    /// <summary>
    /// Convenience base implementation wired for typical usage:
    /// - auto InstanceId
    /// - Archetype resolved from [WorldObject] attribute by default
    /// - Transform + BodyDescriptor stored as properties
    /// </summary>
    public abstract class WorldObject3D : IWorldObject3D
    {
        [VaultIgnore]
        public string InstanceId { get; set; } = Guid.NewGuid().ToString("N");

        /// <summary>
        /// By default resolves the archetype from [WorldObject] on the concrete type.
        /// Override if you need something custom.
        /// </summary>
        [VaultColumn("archetype")]
        public virtual string? ObjectArchetype
        {
            get;
            set;
        }

        [VaultIgnore]
        public Transform3D Transform { get; set; }

        [VaultIgnore]
        public string ZoneId { get; set; } = string.Empty;

        /// <summary>
        /// Engine-agnostic body descriptor associated with this world object, if any.
        /// </summary>
        [VaultIgnore]
        public PhysxBody3DDesc? BodyDescriptor { get; set; }

        [VaultIgnore]
        public IEnumerable<PhysxCollider3DDesc> ColliderDescriptors { get; set; }

        [VaultIgnore]
        public IPhysxBody3D? Body { get; set; }

        [VaultIgnore]
        public IEnumerable<IPhysxCollider3D> Colliders { get; set; }

        [VaultIgnore]
        public bool Expired { get; set; }

        [VaultIgnore]
        public string ClientId { get; set; } = "";

        protected WorldObject3D(Transform3D transform, string zoneId = "", string? archetype = null)
        {
            Transform = transform;
            ZoneId = zoneId;
            ObjectArchetype = archetype;
            ColliderDescriptors = Enumerable.Empty<PhysxCollider3DDesc>();
            Colliders = Enumerable.Empty<IPhysxCollider3D>();
        }

        public override string ToString()
        {
            var p = Transform.Position;
            var sz = Transform.Size;
            var sc = Transform.Scale;

            var collidersStr = string.Join(", ", ColliderDescriptors.Select(c => c.ToString()));

            return
                $"{GetType().Name}(" +
                $"Id={InstanceId}, " +
                $"Archetype={ObjectArchetype ?? "<none>"}, " +
                $"ZoneId={ZoneId}, " +
                $"Pos=({p.X},{p.Y},{p.Z}), " +
                $"Size=({sz.X:0.##},{sz.Y:0.##},{sz.Z:0.##}), " +
                $"Scale=({sc.X:0.##},{sc.Y:0.##},{sc.Z:0.##})," +
                $"Colliders=[{collidersStr}])";
        }

        public virtual Task StepAsync(float dt, IWorldPhysics3D worldPhysics) { return Task.CompletedTask; }
    }

    public class AnonymousWorldObject3D : WorldObject3D
    {
        public AnonymousWorldObject3D(Transform3D transform, PhysxBody3DDesc? bodyDescriptor = null, string zoneId = "", string? archetype = null)
            : base(transform, zoneId: zoneId, archetype: archetype)
        {
            BodyDescriptor = bodyDescriptor;
        }
    }
}

