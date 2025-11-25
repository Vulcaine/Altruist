/*
Copyright 2025 Aron Gere
Licensed under the Apache License, Version 2.0
*/

using System.Numerics;
using System.Reflection;
using System.Text.Json;

using Altruist.Physx.Contracts;
using Altruist.Physx.ThreeD;
using Altruist.ThreeD.Numerics;

namespace Altruist.Gaming.ThreeD
{
    public interface IWorldLoader3D
    {
        /// <summary>
        /// Load a game world manager from a JSON string and a WorldIndex3D descriptor.
        /// This will:
        ///  - create the physics world
        ///  - build all bodies and colliders from the schema
        ///  - instantiate archetype-based world objects
        ///  - create & initialize a GameWorldManager3D
        ///  - add all spawned world objects as static objects to the manager
        /// </summary>
        IGameWorldManager3D LoadFromJson(IWorldIndex3D index, string json);

        /// <summary>
        /// Load a game world manager from the JSON file path defined in the WorldIndex3D descriptor.
        /// Same behavior as LoadFromJson, but reads the JSON from disk.
        /// </summary>
        IGameWorldManager3D LoadFromIndex(IWorldIndex3D index);

        /// <summary>
        /// Optional: world entities instantiated during load (only those with matching archetypes).
        /// These are also added as static objects to the created GameWorldManager3D.
        /// </summary>
        IReadOnlyList<IWorldObject3D> SpawnedWorldObjects { get; }
    }

    /// <summary>
    /// Loads a hierarchical WorldSchema (exported from Unity) into a 3D physics world,
    /// using only Physx abstractions (no direct BEPU types).
    ///
    /// It is also archetype-aware:
    ///   - discovers [WorldObject("Archetype")] types once
    ///   - when a JSON node has an "archetype", it tries to instantiate that world object type
    ///     and associate it with the created physics body
    ///
    /// This refactored version produces a fully wired GameWorldManager3D instead of a bare PhysxWorld3D.
    /// </summary>
    [Service(typeof(IWorldLoader3D))]
    public sealed class WorldLoader3D : IWorldLoader3D
    {
        private readonly IPhysxWorldEngineFactory3D _engineFactory;
        private readonly IPhysxBodyApiProvider3D _bodyApi;
        private readonly IPhysxColliderApiProvider3D _colliderApi;
        private readonly IWorldPartitioner3D _worldPartitioner;

        // archetype name (case-insensitive) -> world object CLR type
        private readonly Dictionary<string, Type> _archetypeMap;

        private readonly List<IWorldObject3D> _spawnedWorldObjects = new();
        public IReadOnlyList<IWorldObject3D> SpawnedWorldObjects => _spawnedWorldObjects;

        public WorldLoader3D(
            IPhysxWorldEngineFactory3D engineFactory,
            IPhysxBodyApiProvider3D bodyApi,
            IPhysxColliderApiProvider3D colliderApi,
            IWorldPartitioner3D worldPartitioner)
        {
            _engineFactory = engineFactory ?? throw new ArgumentNullException(nameof(engineFactory));
            _bodyApi = bodyApi ?? throw new ArgumentNullException(nameof(bodyApi));
            _colliderApi = colliderApi ?? throw new ArgumentNullException(nameof(colliderApi));
            _worldPartitioner = worldPartitioner ?? throw new ArgumentNullException(nameof(worldPartitioner));

            _archetypeMap = BuildArchetypeMap();
        }

        // --------------------------------------------------------------------
        // JSON entrypoints -> GameWorldManager3D
        // --------------------------------------------------------------------

        public IGameWorldManager3D LoadFromJson(IWorldIndex3D index, string json)
        {
            if (index is null)
                throw new ArgumentNullException(nameof(index));

            if (string.IsNullOrWhiteSpace(json))
                throw new ArgumentException("World JSON content cannot be null or empty.", nameof(json));

            var options = new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            };

            var worldSchema = JsonSerializer.Deserialize<WorldSchema>(json, options)
                              ?? throw new InvalidOperationException("Failed to deserialize world JSON into WorldSchema.");

            return BuildGameWorld(index, worldSchema);
        }

        public IGameWorldManager3D LoadFromIndex(IWorldIndex3D index)
        {
            if (index is null)
                throw new ArgumentNullException(nameof(index));

            if (string.IsNullOrWhiteSpace(index.DataPath))
            {
                _spawnedWorldObjects.Clear();

                var engine = _engineFactory.Create(index.Gravity, index.FixedDeltaTime);
                var physxWorld = new PhysxWorld3D(engine);

                var manager = new GameWorldManager3D(index, physxWorld, _worldPartitioner);
                return manager;
            }

            if (!File.Exists(index.DataPath))
                throw new FileNotFoundException("World JSON file not found.", index.DataPath);

            var json = File.ReadAllText(index.DataPath);
            return LoadFromJson(index, json);
        }

        // --------------------------------------------------------------------
        // Core: build physics world + manager + populate static objects
        // --------------------------------------------------------------------

        private IGameWorldManager3D BuildGameWorld(IWorldIndex3D index, WorldSchema worldSchema)
        {
            if (worldSchema is null)
                throw new ArgumentNullException(nameof(worldSchema));

            _spawnedWorldObjects.Clear();

            // 1) Create engine and wrap in PhysxWorld3D
            var engine = _engineFactory.Create(index.Gravity, index.FixedDeltaTime);
            var physxWorld = new PhysxWorld3D(engine);

            // 2) Root "world" transform from exported landscape
            var worldRoot = new AccumulatedTransform(
                worldSchema.Transform.Position,
                EulerToQuaternion(worldSchema.Transform.RotationEuler),
                worldSchema.Transform.Scale
            );

            // 3) Recursively build bodies from all root objects
            foreach (var rootObj in worldSchema.Objects)
            {
                BuildBodiesRecursive(engine, rootObj, worldRoot, physxWorld);
            }

            // 4) Create and initialize the GameWorldManager3D
            var manager = new GameWorldManager3D(index, physxWorld, _worldPartitioner);

            // 5) Add all spawned world objects as static objects into the manager
            foreach (var obj in _spawnedWorldObjects)
            {
                manager.AddStaticObject(obj);
            }

            return manager;
        }

        // --------------------------------------------------------------------
        // Recursion / transforms
        // --------------------------------------------------------------------

        private readonly struct AccumulatedTransform
        {
            public Vector3 Position { get; }
            public Quaternion Rotation { get; }
            public Vector3 Scale { get; }

            public AccumulatedTransform(Vector3 position, Quaternion rotation, Vector3 scale)
            {
                Position = position;
                Rotation = rotation;
                Scale = scale;
            }
        }

        private void BuildBodiesRecursive(
            IPhysxWorldEngine3D engine,
            WorldObjectSchema node,
            AccumulatedTransform parent,
            IPhysxWorld3D world)
        {
            // Compose parent + local -> world
            var localRot = EulerToQuaternion(node.RotationEuler);
            var localPos = node.Position;
            var localScale = node.Scale;

            // Scale composition: component-wise
            var worldScale = new Vector3(
                parent.Scale.X * localScale.X,
                parent.Scale.Y * localScale.Y,
                parent.Scale.Z * localScale.Z
            );

            // Position: scale local by parent scale, then rotate by parent rot, then offset by parent pos
            var scaledLocalPos = new Vector3(
                localPos.X * parent.Scale.X,
                localPos.Y * parent.Scale.Y,
                localPos.Z * parent.Scale.Z
            );
            var rotatedLocalPos = Vector3.Transform(scaledLocalPos, parent.Rotation);
            var worldPos = parent.Position + rotatedLocalPos;

            // Rotation: local then parent (Unity-style)
            var worldRot = Quaternion.Normalize(localRot * parent.Rotation);

            var worldTransform = new AccumulatedTransform(worldPos, worldRot, worldScale);

            // Create static bodies for all colliders on this node
            if (node.Colliders is { Count: > 0 })
            {
                foreach (var colliderSchema in node.Colliders)
                {
                    if (!TryMapShape(colliderSchema.Shape, out var shape))
                        continue; // unknown or unsupported

                    var colliderTransform = BuildColliderTransform(worldTransform, colliderSchema);

                    // Build engine-agnostic collider descriptor
                    var colliderDesc = PhysxCollider3D.Create(shape, colliderTransform, isTrigger: false);
                    // Create collider via collider API provider
                    var collider = _colliderApi.CreateCollider(colliderDesc);

                    // Build engine-agnostic body descriptor (static, mass 0)
                    var bodyDesc = PhysxBody3D.Create(
    PhysxBodyType.Static,
    mass: 0f,
    transform: colliderTransform
);

                    // Create engine-specific body from descriptor
                    var body = _bodyApi.CreateBody(engine, bodyDesc);

                    // Associate collider with body
                    _bodyApi.AddCollider(engine, body, collider);
                    world.AddBody(body);

                    // If this node has an archetype and we know a CLR type for it,
                    // instantiate a world object and attach the *descriptor*.
                    if (!string.IsNullOrWhiteSpace(node.Archetype) &&
                        TryCreateWorldObject(node, colliderTransform, bodyDesc, out var obj))
                    {
                        _spawnedWorldObjects.Add(obj);
                    }
                }
            }

            // Recurse into children
            if (node.Children is { Count: > 0 })
            {
                foreach (var child in node.Children)
                {
                    BuildBodiesRecursive(engine, child, worldTransform, world);
                }
            }
        }

        private bool TryCreateWorldObject(
            WorldObjectSchema node,
            Transform3D transform,
            PhysxBody3DDesc bodyDesc,
            out IWorldObject3D obj)
        {
            obj = default!;

            if (string.IsNullOrWhiteSpace(node.Archetype))
                return false;

            if (!_archetypeMap.TryGetValue(node.Archetype.Trim(), out var type))
                return false;

            var defaultCtor = type.GetConstructor(Type.EmptyTypes);
            if (defaultCtor == null)
            {
                throw new InvalidOperationException(
                    $"World object type '{type.FullName}' must have a parameterless constructor.");
            }

            var instance = defaultCtor.Invoke(null);

            if (instance is not IWorldObject3D worldObj)
                throw new InvalidOperationException(
                    $"World object type '{type.FullName}' must implement IWorldObject3D.");

            var transformProp = type.GetProperty(
                nameof(IWorldObject3D.Transform),
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

            var setter = transformProp?.GetSetMethod(nonPublic: true);
            if (setter == null)
            {
                throw new InvalidOperationException(
                    $"World object type '{type.FullName}' must expose a settable Transform property (at least protected).");
            }

            setter.Invoke(worldObj, [transform]);

            var roomIdProp = type.GetProperty(
                nameof(IWorldObject.ZoneId),
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

            var roomSetter = roomIdProp?.GetSetMethod(nonPublic: true);
            if (roomSetter != null)
            {
                roomSetter.Invoke(worldObj, [string.Empty]);
            }

            worldObj.BodyDescriptor = bodyDesc;

            obj = worldObj;
            return true;
        }

        private static bool TryMapShape(string shapeString, out PhysxColliderShape3D shape)
        {
            shape = default;

            if (string.IsNullOrWhiteSpace(shapeString))
                return false;

            switch (shapeString.Trim().ToLowerInvariant())
            {
                case "box":
                case "mesh":
                    shape = PhysxColliderShape3D.Box3D;
                    return true;

                case "sphere":
                    shape = PhysxColliderShape3D.Sphere3D;
                    return true;

                case "capsule":
                    shape = PhysxColliderShape3D.Capsule3D;
                    return true;

                default:
                    return false;
            }
        }

        // --------------------------------------------------------------------
        // Collider -> Transform3D mapping (engine-agnostic)
        // --------------------------------------------------------------------

        private static Transform3D BuildColliderTransform(
            AccumulatedTransform objectWorld,
            WorldColliderSchema collider)
        {
            var worldScale = objectWorld.Scale;
            var worldRot = objectWorld.Rotation;
            var worldPos = objectWorld.Position;

            // Local center -> world offset
            var centerLocal = collider.Center ?? Vector3.Zero;
            var centerScaled = new Vector3(
                centerLocal.X * worldScale.X,
                centerLocal.Y * worldScale.Y,
                centerLocal.Z * worldScale.Z
            );
            var centerOffsetWorld = Vector3.Transform(centerScaled, worldRot);
            var colliderPositionWorld = worldPos + centerOffsetWorld;

            Size3D size;
            switch (collider.Shape)
            {
                case "box":
                case "mesh": // approximate mesh by a box using its bounds
                    {
                        // Unity BoxCollider.size is FULL size in local space => convert to half extents
                        var sizeLocal = collider.Size ?? new Vector3(1f, 1f, 1f);
                        var fullWorld = new Vector3(
                            sizeLocal.X * worldScale.X,
                            sizeLocal.Y * worldScale.Y,
                            sizeLocal.Z * worldScale.Z
                        );
                        var half = fullWorld * 0.5f;
                        size = new Size3D(half.X, half.Y, half.Z);
                        break;
                    }

                case "sphere":
                    {
                        var radiusLocal = collider.Radius ?? 0.5f;
                        var maxScale = Math.Max(worldScale.X, Math.Max(worldScale.Y, worldScale.Z));
                        var radiusWorld = radiusLocal * maxScale;
                        size = new Size3D(radiusWorld, 0f, 0f);
                        break;
                    }

                case "capsule":
                    {
                        var radiusLocal = collider.Radius ?? 0.5f;
                        var heightLocal = collider.Height ?? 1f;
                        var dir = collider.Direction ?? 1; // Unity: 0=X,1=Y,2=Z

                        float axisScale = dir switch
                        {
                            0 => worldScale.X,
                            1 => worldScale.Y,
                            2 => worldScale.Z,
                            _ => worldScale.Y
                        };

                        var radialScale = dir switch
                        {
                            0 => Math.Max(worldScale.Y, worldScale.Z),
                            1 => Math.Max(worldScale.X, worldScale.Z),
                            2 => Math.Max(worldScale.X, worldScale.Y),
                            _ => Math.Max(worldScale.X, worldScale.Z)
                        };

                        var radiusWorld = radiusLocal * radialScale;
                        var heightWorld = heightLocal * axisScale;
                        var halfLength = heightWorld * 0.5f;

                        size = new Size3D(radiusWorld, halfLength, 0f);
                        break;
                    }

                default:
                    // Unknown -> default to 1x1x1 box
                    size = new Size3D(0.5f, 0.5f, 0.5f);
                    break;
            }

            var position3D = new Position3D(
                colliderPositionWorld.X,
                colliderPositionWorld.Y,
                colliderPositionWorld.Z);

            var rotation3D = Rotation3D.FromQuaternion(objectWorld.Rotation);
            var scale3D = Scale3D.One;

            return new Transform3D(position3D, size, scale3D, rotation3D);
        }

        // --------------------------------------------------------------------
        // Archetype discovery
        // --------------------------------------------------------------------

        private static Dictionary<string, Type> BuildArchetypeMap()
        {
            var map = new Dictionary<string, Type>(StringComparer.OrdinalIgnoreCase);

            var assemblies = AppDomain.CurrentDomain.GetAssemblies()
                .Where(a => !a.IsDynamic && !string.IsNullOrWhiteSpace(a.FullName))
                .ToArray();

            foreach (var t in TypeDiscovery.FindTypesWithAttribute<WorldObjectAttribute>(assemblies))
            {
                if (!typeof(IWorldObject3D).IsAssignableFrom(t))
                    continue;

                var attr = t.GetCustomAttribute<WorldObjectAttribute>(inherit: false);
                if (attr == null || string.IsNullOrWhiteSpace(attr.Archetype))
                    continue;

                var key = attr.Archetype.Trim();
                if (!map.ContainsKey(key))
                {
                    map[key] = t;
                }
            }

            return map;
        }

        // --------------------------------------------------------------------
        // Math helpers
        // --------------------------------------------------------------------

        private static Quaternion EulerToQuaternion(Vector3 eulerDegrees)
        {
            // Unity's eulerAngles are in degrees; System.Numerics uses radians.
            // Convention: yaw = Y, pitch = X, roll = Z.
            float yaw = DegToRad(eulerDegrees.Y);
            float pitch = DegToRad(eulerDegrees.X);
            float roll = DegToRad(eulerDegrees.Z);

            return Quaternion.CreateFromYawPitchRoll(yaw, pitch, roll);
        }

        private static float DegToRad(float deg) => (float)(Math.PI / 180.0) * deg;
    }
}
