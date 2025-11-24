/*
Copyright 2025 Aron Gere
Licensed under the Apache License, Version 2.0
*/

using Altruist.Physx.ThreeD;
using Altruist.ThreeD.Numerics;

namespace Altruist.Gaming.World.ThreeD
{
    using Altruist.Gaming.World;

    /// <summary>
    /// Contract for a 3D world entity that can live in partitions
    /// and be associated with a physics body.
    /// </summary>
    public interface IWorldObject3D : IWorldObject
    {
        /// <summary>Transform in world space.</summary>
        Transform3D Transform { get; }

        /// <summary>
        /// Optional physics body backing this object.
        /// Static world geometry might be static bodies; dynamic objects might swap bodies.
        /// </summary>
        IPhysxBody3D? Body { get; set; }
    }

    /// <summary>
    /// Convenience base implementation wired for typical usage:
    /// - auto InstanceId
    /// - Archetype resolved from [WorldObject] attribute by default
    /// - Transform + Body stored as properties
    /// </summary>
    public abstract class WorldObject3D : IWorldObject3D
    {
        public string InstanceId { get; protected set; } = Guid.NewGuid().ToString("N");

        /// <summary>
        /// By default resolves the archetype from [WorldObject] on the concrete type.
        /// Override if you need something custom.
        /// </summary>
        public virtual string Archetype
        {
            get
            {
                var attr = (WorldObjectAttribute?)Attribute.GetCustomAttribute(
                    GetType(),
                    typeof(WorldObjectAttribute),
                    inherit: false);

                if (attr == null)
                    throw new InvalidOperationException(
                        $"Type {GetType().FullName} must be annotated with [WorldObject(\"ArchetypeName\")] " +
                        "or override the Archetype property.");

                return attr.Archetype;
            }
        }

        public Transform3D Transform { get; protected set; }
        public string RoomId { get; protected set; } = string.Empty;

        public IPhysxBody3D? Body { get; set; }

        protected WorldObject3D(Transform3D transform, string roomId = "")
        {
            Transform = transform;
            RoomId = roomId ?? string.Empty;
        }

        /// <summary>
        /// Helper to update transform when physics has moved the body.
        /// Concrete types can expose richer APIs on top.
        /// </summary>
        public virtual void SyncFromBody()
        {
            if (Body == null)
                return;

            var pos = Body.Position;
            var rot = Body.Rotation;

            Transform = Transform.WithPosition(
                new Position3D(pos.X, pos.Y, pos.Z)
            ).WithRotation(
                Rotation3D.FromQuaternion(rot)
            );
        }
    }
}
