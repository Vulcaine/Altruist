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
    /// <para>Derive from <see cref="WorldObject2D"/> for runtime entities (players, NPCs, projectiles), from
    /// <see cref="WorldObjectPrefab2D"/> when the object is also persisted as a prefab row, or use
    /// <see cref="AnonymousWorldObject2D"/> for ad-hoc objects without a class. Add objects with
    /// <see cref="IGameWorldManager2D.SpawnDynamicObject"/> / <see cref="IGameWorldManager2D.SpawnStaticObject"/>;
    /// the organizer then calls <c>Step(dt, world)</c> every tick and copies <see cref="Body"/>'s position into
    /// <see cref="Transform"/> after physics. The 3D counterpart is <see cref="Altruist.Gaming.ThreeD.IWorldObject3D"/>.</para>
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
    /// <para>Override <c>Step</c> for per-tick behaviour (runs on the world tick thread, before physics).
    /// Set <c>Expired = true</c> to have the organizer destroy the object on its next tick.</para>
    /// <example><code>
    /// [WorldObject("crate")]
    /// public sealed class Crate : WorldObject2D
    /// {
    ///     public Crate(Transform2D t) : base(t) { }
    ///     public override void Step(float dt, IGameWorldManager2D world)
    ///     {
    ///         if (Transform.Position.Y &lt; -100) Expired = true;
    ///     }
    /// }
    /// </code></example>
    /// </summary>
    public abstract class WorldObject2D : IWorldObject2D
    {
        /// <summary>Unique id in the world (a new GUID, "N" format, by default).</summary>
        [VaultIgnore]
        public string InstanceId { get; set; } = Guid.NewGuid().ToString("N");

        /// <summary>
        /// By default resolves the archetype from [WorldObject] on the concrete type.
        /// Override if you need something custom.
        /// <para>Note: <see cref="GameWorldManager2D"/> overwrites this on spawn with the
        /// <see cref="WorldObjectAttribute"/> value (or <c>""</c>) for every type except
        /// <see cref="AnonymousWorldObject2D"/>, so the constructor's <c>archetype</c> argument only sticks for
        /// anonymous objects or overrides that ignore the setter.</para>
        /// </summary>
        public virtual string? ObjectArchetype
        {
            get;
            set;
        }

        /// <inheritdoc/>
        [VaultIgnore]
        public Transform2D Transform { get; set; }

        /// <summary>Room / zone id used by room-scoped queries (<c>""</c> = none).</summary>
        [VaultIgnore]
        public string ZoneId { get; set; } = string.Empty;

        /// <inheritdoc/>
        [VaultIgnore]
        public IPhysxBody2D? Body { get; set; }

        /// <summary>Set to true to have the organizer destroy the object at the start of its next tick.</summary>
        [VaultIgnore]
        public bool Expired { get; set; }

        /// <inheritdoc/>
        [VaultIgnore]
        public string ClientId { get; set; } = "";

        /// <summary>Initializes the object.</summary>
        /// <param name="transform">Initial transform (position, size, rotation) in world space.</param>
        /// <param name="zoneId">Room / zone id (null becomes <c>""</c>).</param>
        /// <param name="archetype">Initial archetype (see the note on <see cref="ObjectArchetype"/>).</param>
        protected WorldObject2D(Transform2D transform, string zoneId = "", string? archetype = null)
        {
            Transform = transform;
            ZoneId = zoneId ?? string.Empty;
            ObjectArchetype = archetype;
        }

        /// <summary>Per-tick hook (default: nothing). Called by the organizer before physics.</summary>
        /// <param name="dt">Frame time in seconds.</param>
        /// <param name="world">The world the object lives in.</param>
        public virtual void Step(float dt, IGameWorldManager2D world)
        {
            return;
        }
    }

    /// <summary>
    /// Base for 2D prefabs that also behave as world object descriptors.
    /// - Inherits PrefabModel for persistence
    /// - Implements IWorldObject2D for runtime/world indexing
    /// <para>Only the archetype (column <c>archetype</c>) and the prefab's own columns are persisted; runtime
    /// state (transform, body, zone, client) is <c>[VaultIgnore]</c>. Use <see cref="WorldObject2D"/> when the
    /// object is not stored.</para>
    /// </summary>
    public abstract class WorldObjectPrefab2D : PrefabModel, IWorldObject2D
    {
        /// <inheritdoc/>
        [VaultIgnore]
        public string ClientId { get; set; } = "";

        /// <summary>Unique id in the world (a new GUID by default).</summary>
        [VaultIgnore]
        public string InstanceId { get; set; } = Guid.NewGuid().ToString();

        /// <summary>Archetype, persisted in the <c>archetype</c> column (overwritten on spawn, see <see cref="WorldObject2D.ObjectArchetype"/>).</summary>
        [VaultColumn("archetype")]
        public string? ObjectArchetype { get; set; } = null;

        /// <inheritdoc/>
        [VaultIgnore]
        public Transform2D Transform { get; set; }

        /// <inheritdoc/>
        [VaultIgnore]
        public IPhysxBody2D? Body { get; set; }

        /// <summary>Room / zone id used by room-scoped queries (<c>""</c> = none).</summary>
        [VaultIgnore]
        public string ZoneId { get; set; } = "";

        /// <summary>Set to true to have the organizer destroy the object at the start of its next tick.</summary>
        [VaultIgnore]
        public bool Expired { get; set; }

        /// <summary>Per-tick hook (default: nothing). Called by the organizer before physics.</summary>
        /// <param name="dt">Frame time in seconds.</param>
        /// <param name="world">The world the object lives in.</param>
        public virtual void Step(float dt, IGameWorldManager2D world) { }
    }

    /// <summary>
    /// Concrete world object created dynamically at runtime (not loaded from a schema).
    /// <para>Its archetype is the one passed in (the world does not overwrite it), which makes it the way
    /// to spawn queryable objects without writing a class; <see cref="WorldLoader2D"/> also uses it for
    /// nodes whose archetype matches no <see cref="WorldObjectAttribute"/> type.</para>
    /// </summary>
    public class AnonymousWorldObject2D : WorldObject2D
    {
        /// <summary>Creates an anonymous object.</summary>
        /// <param name="transform">Initial transform in world space.</param>
        /// <param name="zoneId">Room / zone id (<c>""</c> = none).</param>
        /// <param name="archetype">Archetype kept as-is on spawn (null = none).</param>
        public AnonymousWorldObject2D(Transform2D transform, string zoneId = "", string? archetype = null)
            : base(transform, zoneId: zoneId, archetype: archetype)
        {
        }
    }
}
