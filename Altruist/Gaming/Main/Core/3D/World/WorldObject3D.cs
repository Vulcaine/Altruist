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
    /// <remarks>
    /// Use it when the world object is also a persisted prefab (<see cref="PrefabModel"/>, only <c>archetype</c> is stored; all
    /// runtime fields are <c>[VaultIgnore]</c>). For plain runtime objects derive from <see cref="WorldObject3D"/> instead.
    /// Bodies are created by <see cref="IGameWorldManager3D"/> spawn methods from <see cref="BodyDescriptor"/>/<see cref="ColliderDescriptors"/>.
    /// </remarks>
    public abstract class WorldObjectPrefab3D : PrefabModel, IWorldObject3D
    {
        /// <summary>Connection id of the owning client; empty for server-owned objects.</summary>
        [VaultIgnore]
        public string ClientId { get; set; } = "";

        // IWorldObject: InstanceId, RoomId come from Prefab3D already.
        // Prefab3D has:
        //   public virtual string InstanceId { get; set; }
        //   public virtual string RoomId { get; set; }
        /// <summary>Unique instance id (new GUID per instance).</summary>
        [VaultIgnore]
        public string InstanceId { get; set; } = Guid.NewGuid().ToString();

        /// <summary>Network-friendly sequential ID. Auto-assigned by the world manager on spawn.</summary>
        [VaultIgnore]
        public uint VirtualId { get; set; }

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

        /// <summary>Runtime physics body, set by the world manager on spawn (<c>null</c> for lightweight objects).</summary>
        [VaultIgnore]
        public IPhysxBody3D? Body { get; set; }

        /// <summary>Runtime colliders created from <see cref="ColliderDescriptors"/> on spawn.</summary>
        [VaultIgnore]
        public IEnumerable<IPhysxCollider3D> Colliders { get; set; } = Enumerable.Empty<IPhysxCollider3D>();

        /// <summary>Room/zone id used to shard queries (e.g. <see cref="IGameWorldManager3D.GetNearbyObjectsInRoom"/>); empty by default.</summary>
        [VaultIgnore]
        public string ZoneId { get; set; } = "";

        /// <summary>Collision layer bitmask (default all bits set).</summary>
        [VaultIgnore]
        public uint CollisionLayer { get; set; } = 0xFFFFFFFFu;

        /// <summary>Set to <c>true</c> to have the organizer destroy the object at the start of the next world step.</summary>
        [VaultIgnore]
        public bool Expired { get; set; }

        /// <summary>Per-frame hook called by the world organizer before physics; default does nothing.</summary>
        /// <param name="dt">Elapsed time in seconds.</param>
        /// <param name="world">The world the object lives in.</param>
        public virtual void Step(float dt, IGameWorldManager3D world) { return; }
    }

    /// <summary>
    /// Optional interface for world objects that have a vnum (template ID).
    /// Used by the hibernation system to track entity types.
    /// </summary>
    public interface IVnumProvider
    {
        /// <summary>Template id of the entity (recorded when it is hibernated).</summary>
        int Vnum { get; }
    }

    /// <summary>
    /// Contract for a 3D world entity that can live in partitions
    /// and be associated with a physics body descriptor.
    /// </summary>
    /// <remarks>
    /// Implement it via <see cref="WorldObject3D"/> (runtime objects) or <see cref="WorldObjectPrefab3D"/> (persisted prefabs) and
    /// spawn with <see cref="IGameWorldManager3D.SpawnObject"/>. The archetype is taken from <see cref="WorldObjectAttribute"/> on
    /// the concrete class at spawn. Mark the class <c>[Synchronized]</c> and implement <c>ISynchronizedEntity</c> for automatic sync.
    /// For 2D use <see cref="Altruist.Gaming.TwoD.IWorldObject2D"/>.
    /// </remarks>
    /// <example>
    /// <code>
    /// [WorldObject("monster")]
    /// public sealed class Monster : WorldObject3D
    /// {
    ///     public Monster(Transform3D t) : base(t) { }
    ///     public override void Step(float dt, IGameWorldManager3D world) { /* move, think */ }
    /// }
    /// await world.SpawnObject(new Monster(spawnTransform));
    /// </code>
    /// </example>
    public interface IWorldObject3D : IWorldObject<IGameWorldManager3D>
    {
        /// <summary>
        /// Connection id of the controlling client. Non-empty marks a player-owned object (network destination for visibility
        /// and self-sync); empty for server-owned objects.
        /// </summary>
        [VaultIgnore]
        public string ClientId { get; set; }

        /// <summary>Network-friendly sequential ID. Auto-assigned by the world manager on spawn.</summary>
        uint VirtualId { get; set; }

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

        /// <summary>
        /// Engine-agnostic collider descriptors. On spawn, the first non-trigger descriptor also defines the object's partition
        /// bounds; when empty, physics spawns use a default box at <see cref="Transform"/>.
        /// </summary>
        [VaultIgnore]
        IEnumerable<PhysxCollider3DDesc> ColliderDescriptors { get; set; }

        /// <summary>Runtime colliders created from <see cref="ColliderDescriptors"/> on spawn (when physics is enabled).</summary>
        IEnumerable<IPhysxCollider3D> Colliders { get; set; }

        /// <summary>
        /// Runtime body set by the world manager on spawn: an engine body, an in-memory body (no physics engine), or <c>null</c>
        /// (lightweight objects). The organizer copies its position/rotation into <see cref="Transform"/> after each physics step.
        /// </summary>
        [VaultIgnore]
        public IPhysxBody3D? Body { get; set; }

        /// <summary>
        /// Collision layer bitmask for this entity. Used by SpatialCollisionDispatcher
        /// to filter which entities can collide with each other.
        /// Default: PhysxLayer.All (collides with everything).
        /// Set to a specific layer to control interactions.
        /// </summary>
        [VaultIgnore]
        uint CollisionLayer { get; set; }
    }

    /// <summary>
    /// Allows objects whose gameplay pivot differs from the physics body center
    /// to expose the transform that should be written back to the world object.
    /// </summary>
    public interface IPhysicsTransformSync3D
    {
        /// <summary>Returns the world transform to assign to the object after a physics step, given its body.</summary>
        /// <param name="body">The object's physics body after the step.</param>
        /// <returns>The transform written to <c>IWorldObject3D.Transform</c>.</returns>
        Transform3D GetWorldTransformFromPhysics(IPhysxBody3D body);
    }

    /// <summary>
    /// Convenience base implementation wired for typical usage:
    /// - auto InstanceId
    /// - Archetype resolved from [WorldObject] attribute by default
    /// - Transform + BodyDescriptor stored as properties
    /// </summary>
    /// <remarks>
    /// The default base for runtime 3D world objects; override <see cref="Step"/> for per-frame logic. Use
    /// <see cref="WorldObjectPrefab3D"/> when the object must be persisted, or <see cref="AnonymousWorldObject3D"/> for ad-hoc objects
    /// without a dedicated class. Note: the archetype passed to the constructor is replaced on spawn by the class's
    /// <see cref="WorldObjectAttribute"/> archetype (or <c>""</c>), except for <see cref="AnonymousWorldObject3D"/>.
    /// </remarks>
    public abstract class WorldObject3D : IWorldObject3D
    {
        /// <summary>Unique instance id (GUID, <c>"N"</c> format).</summary>
        [VaultIgnore]
        public string InstanceId { get; set; } = Guid.NewGuid().ToString("N");

        /// <summary>
        /// Network-friendly sequential ID. Auto-assigned by the world manager on spawn.
        /// Used for compact wire protocol references. Preserved during hibernation.
        /// Freed when the entity is destroyed.
        /// </summary>
        [VaultIgnore]
        public uint VirtualId { get; set; }

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

        /// <inheritdoc/>
        [VaultIgnore]
        public Transform3D Transform { get; set; }

        /// <inheritdoc/>
        [VaultIgnore]
        public string ZoneId { get; set; } = string.Empty;

        /// <inheritdoc/>
        [VaultIgnore]
        public uint CollisionLayer { get; set; } = 0xFFFFFFFFu; // PhysxLayer.All

        /// <summary>
        /// Engine-agnostic body descriptor associated with this world object, if any.
        /// </summary>
        [VaultIgnore]
        public PhysxBody3DDesc? BodyDescriptor { get; set; }

        /// <inheritdoc/>
        [VaultIgnore]
        public IEnumerable<PhysxCollider3DDesc> ColliderDescriptors { get; set; }

        /// <inheritdoc/>
        [VaultIgnore]
        public IPhysxBody3D? Body { get; set; }

        /// <inheritdoc/>
        [VaultIgnore]
        public IEnumerable<IPhysxCollider3D> Colliders { get; set; }

        /// <inheritdoc/>
        [VaultIgnore]
        public bool Expired { get; set; }

        /// <inheritdoc/>
        [VaultIgnore]
        public string ClientId { get; set; } = "";

        /// <summary>Initializes the object with empty collider lists.</summary>
        /// <param name="transform">Initial world transform.</param>
        /// <param name="zoneId">Room/zone id (empty for none).</param>
        /// <param name="archetype">Initial archetype (overwritten on spawn unless the object is anonymous).</param>
        protected WorldObject3D(Transform3D transform, string zoneId = "", string? archetype = null)
        {
            Transform = transform;
            ZoneId = zoneId;
            ObjectArchetype = archetype;
            ColliderDescriptors = Enumerable.Empty<PhysxCollider3DDesc>();
            Colliders = Enumerable.Empty<IPhysxCollider3D>();
        }

        /// <inheritdoc/>
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

        /// <summary>Per-frame hook called by the world organizer before physics; default does nothing.</summary>
        /// <param name="dt">Elapsed time in seconds.</param>
        /// <param name="world">The world the object lives in.</param>
        public virtual void Step(float dt, IGameWorldManager3D world) { return; }
    }

    /// <summary>
    /// Concrete <see cref="WorldObject3D"/> for ad-hoc objects (props, triggers, loaded world geometry) that need no class of
    /// their own; its <c>ObjectArchetype</c> is kept as given instead of being resolved from an attribute.
    /// </summary>
    public class AnonymousWorldObject3D : WorldObject3D
    {
        /// <summary>Creates an anonymous object.</summary>
        /// <param name="transform">World transform.</param>
        /// <param name="bodyDescriptor">Optional body descriptor (a non-null value makes <see cref="IGameWorldManager3D.SpawnObject"/> create a body).</param>
        /// <param name="zoneId">Room/zone id.</param>
        /// <param name="archetype">Archetype kept as-is.</param>
        public AnonymousWorldObject3D(Transform3D transform, PhysxBody3DDesc? bodyDescriptor = null, string zoneId = "", string? archetype = null)
            : base(transform, zoneId: zoneId, archetype: archetype)
        {
            BodyDescriptor = bodyDescriptor;
        }
    }
}
