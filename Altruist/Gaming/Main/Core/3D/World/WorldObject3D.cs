/*
Copyright 2025 Aron Gere
Licensed under the Apache License, Version 2.0
*/

using System.Text.Json.Serialization;

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
        // IWorldObject: InstanceId, RoomId come from Prefab3D already.
        // Prefab3D has:
        //   public virtual string InstanceId { get; set; }
        //   public virtual string RoomId { get; set; }

        public string InstanceId { get; set; } = Guid.NewGuid().ToString();

        /// <summary>
        /// Archetype is resolved from [WorldObject] attribute by default.
        /// Override only if you need something special.
        /// </summary>
        public string? Archetype { get; set; } = null;

        /// <summary>
        /// Transform in world space. Prefab3D already has this property.
        /// </summary>
        public Transform3D Transform { get; set; }

        /// <summary>
        /// Engine-agnostic body descriptor (not persisted).
        /// This is used by SpawnService3D to create the runtime body.
        /// </summary>
        [JsonIgnore]
        [VaultIgnore]
        public PhysxBody3DDesc? BodyDescriptor { get; set; }

        [JsonIgnore]
        [VaultIgnore]
        public string ZoneId { get; set; } = "";
    }

    /// <summary>
    /// Contract for a 3D world entity that can live in partitions
    /// and be associated with a physics body descriptor.
    /// </summary>
    public interface IWorldObject3D : IWorldObject
    {
        /// <summary>Transform in world space.</summary>
        Transform3D Transform { get; set; }

        /// <summary>
        /// Optional physics body descriptor backing this object.
        /// Static world geometry might be static bodies; dynamic objects might swap bodies.
        /// The descriptor is engine/provider agnostic.
        /// </summary>
        PhysxBody3DDesc? BodyDescriptor { get; set; }
    }

    /// <summary>
    /// Convenience base implementation wired for typical usage:
    /// - auto InstanceId
    /// - Archetype resolved from [WorldObject] attribute by default
    /// - Transform + BodyDescriptor stored as properties
    /// </summary>
    public abstract class WorldObject3D : IWorldObject3D
    {
        public string InstanceId { get; set; } = Guid.NewGuid().ToString("N");

        /// <summary>
        /// By default resolves the archetype from [WorldObject] on the concrete type.
        /// Override if you need something custom.
        /// </summary>
        public virtual string? Archetype
        {
            get;
            set;
        }

        public Transform3D Transform { get; set; }
        public string ZoneId { get; set; } = string.Empty;

        /// <summary>
        /// Engine-agnostic body descriptor associated with this world object, if any.
        /// </summary>
        public PhysxBody3DDesc? BodyDescriptor { get; set; }

        protected WorldObject3D(Transform3D transform, string zoneId = "", string? archetype = null)
        {
            Transform = transform;
            ZoneId = zoneId;
            Archetype = archetype;
        }

        public override string ToString()
        {
            var p = Transform.Position;
            var sz = Transform.Size;
            var sc = Transform.Scale;

            return
                $"{GetType().Name}(" +
                $"Id={InstanceId}, " +
                $"Archetype={Archetype ?? "<none>"}, " +
                $"ZoneId={ZoneId}, " +
                $"Pos=({p.X},{p.Y},{p.Z}), " +
                $"Size=({sz.X:0.##},{sz.Y:0.##},{sz.Z:0.##}), " +
                $"Scale=({sc.X:0.##},{sc.Y:0.##},{sc.Z:0.##}))";
        }
    }

    public class AnonymousWorldObject3D : WorldObject3D
    {
        public AnonymousWorldObject3D(Transform3D transform)
            : base(transform, zoneId: string.Empty, archetype: string.Empty)
        {
        }
    }
}

