/*
Copyright 2025 Aron Gere
Licensed under the Apache License, Version 2.0
*/

using System.Numerics;
using System.Reflection;
using System.Text.Json;

using Altruist.Numerics;
using Altruist.Physx;
using Altruist.Physx.TwoD;
using Altruist.TwoD.Numerics;

namespace Altruist.Gaming.TwoD
{
    public interface IWorldLoader2D
    {
        /// <summary>
        /// Load a game world manager from a JSON string and a WorldIndex2D descriptor.
        /// </summary>
        Task<IGameWorldManager2D> LoadFromJson(IWorldIndex2D index, string json);

        /// <summary>
        /// Load a game world manager from the JSON file path defined in the WorldIndex2D descriptor.
        /// </summary>
        Task<IGameWorldManager2D> LoadFromIndex(IWorldIndex2D index);

        IReadOnlyList<IWorldObject2D> SpawnedWorldObjects { get; }
    }

    [Service(typeof(IWorldLoader2D))]
    [ConditionalOnConfig("altruist:game")]
    [ConditionalOnConfig("altruist:environment:mode", havingValue: "2D")]
    public sealed class WorldLoader2D : IWorldLoader2D
    {
        private readonly IPhysxWorldEngineFactory2D _engineFactory;
        private readonly IPhysxBodyApiProvider2D _bodyApi;
        private readonly IPhysxColliderApiProvider2D _colliderApi;
        private readonly IWorldPartitioner2D _worldPartitioner;

        // archetype name (case-insensitive) -> world object CLR type
        private readonly Dictionary<string, Type> _archetypeMap;

        private readonly List<IWorldObject2D> _spawnedWorldObjects = new();
        public IReadOnlyList<IWorldObject2D> SpawnedWorldObjects => _spawnedWorldObjects;

        private readonly JsonSerializerOptions _options;

        public WorldLoader2D(
            IPhysxWorldEngineFactory2D engineFactory,
            IPhysxBodyApiProvider2D bodyApi,
            IPhysxColliderApiProvider2D colliderApi,
            IWorldPartitioner2D worldPartitioner,
            JsonSerializerOptions options)
        {
            _engineFactory = engineFactory;
            _bodyApi = bodyApi;
            _colliderApi = colliderApi;
            _worldPartitioner = worldPartitioner;
            _options = options;
            _archetypeMap = BuildArchetypeMap();
        }

        // ── Entrypoints ──────────────────────────────────────────────────────

        public async Task<IGameWorldManager2D> LoadFromJson(IWorldIndex2D index, string json)
        {
            if (index is null) throw new ArgumentNullException(nameof(index));
            if (string.IsNullOrWhiteSpace(json))
                throw new ArgumentException("World JSON cannot be null or empty.", nameof(json));

            var schema = JsonSerializer.Deserialize<WorldSchema2D>(json, _options)
                         ?? throw new InvalidOperationException("Failed to deserialize WorldSchema2D.");

            index.Size = new IntVector2(
                (int)schema.Transform.Size.X,
                (int)schema.Transform.Size.Y);

            index.Position = schema.Transform.Position.ToNumerics();

            return await BuildGameWorld(index, schema);
        }

        public async Task<IGameWorldManager2D> LoadFromIndex(IWorldIndex2D index)
        {
            if (index is null) throw new ArgumentNullException(nameof(index));

            if (index is WorldIndex2D wi && string.IsNullOrWhiteSpace(wi.DataPath))
            {
                _spawnedWorldObjects.Clear();
                var emptyEngine = _engineFactory.Create(index.Gravity, index.FixedDeltaTime);
                var emptyPhysxWorld = new PhysxWorld2D(emptyEngine);
                return new GameWorldManager2D(index, emptyPhysxWorld, _worldPartitioner,
                    null, _bodyApi, _colliderApi);
            }

            var dataPath = (index as WorldIndex2D)?.DataPath;
            if (dataPath is null)
                throw new InvalidOperationException("IWorldIndex2D implementation does not expose DataPath.");

            if (!File.Exists(dataPath))
                throw new FileNotFoundException("World JSON file not found.", dataPath);

            var json = File.ReadAllText(dataPath);
            return await LoadFromJson(index, json);
        }

        // ── Core build ───────────────────────────────────────────────────────

        private async Task<IGameWorldManager2D> BuildGameWorld(IWorldIndex2D index, WorldSchema2D schema)
        {
            _spawnedWorldObjects.Clear();

            var engine = _engineFactory.Create(index.Gravity, index.FixedDeltaTime);
            var physxWorld = new PhysxWorld2D(engine);

            var manager = new GameWorldManager2D(index, physxWorld, _worldPartitioner,
                null, _bodyApi, _colliderApi);
            manager.Initialize();

            var worldRoot = new AccumulatedTransform2D(
                schema.Transform.Position.ToNumerics(),
                schema.Transform.Rotation);

            foreach (var obj in schema.Objects)
                BuildBodiesRecursive(obj, worldRoot);

            foreach (var obj in _spawnedWorldObjects)
                manager.SpawnStaticObject(obj);

            await Task.CompletedTask;
            return manager;
        }

        // ── Hierarchy traversal ──────────────────────────────────────────────

        private readonly struct AccumulatedTransform2D
        {
            public Vector2 Position { get; }
            public float Rotation { get; }

            public AccumulatedTransform2D(Vector2 position, float rotation)
            {
                Position = position;
                Rotation = rotation;
            }
        }

        private void BuildBodiesRecursive(WorldObjectSchema2D node, AccumulatedTransform2D parent)
        {
            float worldRot = parent.Rotation + node.Rotation;

            float sinR = MathF.Sin(parent.Rotation);
            float cosR = MathF.Cos(parent.Rotation);
            var localPos = node.Position.ToNumerics();
            var rotatedLocal = new Vector2(
                cosR * localPos.X - sinR * localPos.Y,
                sinR * localPos.X + cosR * localPos.Y);

            var worldPos = parent.Position + rotatedLocal;
            var worldTransform = new AccumulatedTransform2D(worldPos, worldRot);

            var objectTransform = BuildObjectTransform(worldTransform, node);
            var colliderParams = BuildColliderParams(node, worldTransform);

            if (colliderParams.Count > 0 &&
                TryCreateWorldObject(node, objectTransform, out var obj))
            {
                _spawnedWorldObjects.Add(obj);
            }

            if (node.Children is { Count: > 0 })
            {
                foreach (var child in node.Children)
                    BuildBodiesRecursive(child, worldTransform);
            }
        }

        private static Transform2D BuildObjectTransform(
            AccumulatedTransform2D worldTransform,
            WorldObjectSchema2D node)
        {
            var pos = Position2D.Of((int)worldTransform.Position.X, (int)worldTransform.Position.Y);
            var size = Size2D.Of(node.Size.X, node.Size.Y);
            var rot = Rotation2D.FromRadians(worldTransform.Rotation);
            return new Transform2D(pos, size, Scale2D.One, rot);
        }

        private static List<PhysxCollider2DParams> BuildColliderParams(
            WorldObjectSchema2D node,
            AccumulatedTransform2D worldTransform)
        {
            var result = new List<PhysxCollider2DParams>();

            if (node.Colliders is not { Count: > 0 })
                return result;

            foreach (var col in node.Colliders)
            {
                if (!TryMapShape(col.Shape, out var shape))
                    continue;

                var center = col.Center?.ToNumerics() ?? Vector2.Zero;
                var colWorldPos = worldTransform.Position + center;
                var colPos = Position2D.Of((int)colWorldPos.X, (int)colWorldPos.Y);

                Size2D colSize;
                switch (shape)
                {
                    case PhysxColliderShape2D.Box2D:
                        var halfSize = (col.Size?.ToNumerics() ?? Vector2.One) * 0.5f;
                        colSize = Size2D.Of(halfSize.X, halfSize.Y);
                        break;

                    case PhysxColliderShape2D.Circle2D:
                        var radius = col.Radius ?? 0.5f;
                        colSize = Size2D.Of(radius, 0f);
                        break;

                    case PhysxColliderShape2D.Capsule2D:
                        var capRadius = col.Radius ?? 0.5f;
                        var halfLen = (col.Height ?? 1f) * 0.5f;
                        colSize = Size2D.Of(capRadius, halfLen);
                        break;

                    default:
                        colSize = Size2D.Of(0.5f, 0.5f);
                        break;
                }

                var colTransform = new Transform2D(
                    colPos,
                    colSize,
                    Scale2D.One,
                    Rotation2D.FromRadians(worldTransform.Rotation));

                result.Add(new PhysxCollider2DParams(shape, colTransform, isTrigger: false));
            }

            return result;
        }

        private bool TryCreateWorldObject(
            WorldObjectSchema2D node,
            Transform2D transform,
            out IWorldObject2D obj)
        {
            if (string.IsNullOrWhiteSpace(node.Archetype) ||
                !_archetypeMap.TryGetValue(node.Archetype.Trim(), out var type))
            {
                obj = new AnonymousWorldObject2D(transform, archetype: node.Archetype ?? string.Empty);
                return true;
            }

            obj = default!;

            var defaultCtor = type.GetConstructor(Type.EmptyTypes);
            if (defaultCtor is null)
                throw new InvalidOperationException(
                    $"World object type '{type.FullName}' must have a parameterless constructor.");

            if (defaultCtor.Invoke(null) is not IWorldObject2D worldObj)
                throw new InvalidOperationException(
                    $"World object type '{type.FullName}' must implement IWorldObject2D.");

            worldObj.Transform = transform;
            if (worldObj is ITypelessWorldObject wo)
                wo.ZoneId = string.Empty;

            obj = worldObj;
            return true;
        }

        private static bool TryMapShape(string shapeString, out PhysxColliderShape2D shape)
        {
            shape = default;
            if (string.IsNullOrWhiteSpace(shapeString))
                return false;

            switch (shapeString.Trim().ToLowerInvariant())
            {
                case "box":
                case "mesh":
                    shape = PhysxColliderShape2D.Box2D;
                    return true;

                case "circle":
                case "sphere":
                    shape = PhysxColliderShape2D.Circle2D;
                    return true;

                case "capsule":
                    shape = PhysxColliderShape2D.Capsule2D;
                    return true;

                default:
                    return false;
            }
        }

        private static Dictionary<string, Type> BuildArchetypeMap()
        {
            var map = new Dictionary<string, Type>(StringComparer.OrdinalIgnoreCase);

            var assemblies = AppDomain.CurrentDomain.GetAssemblies()
                .Where(a => !a.IsDynamic && !string.IsNullOrWhiteSpace(a.FullName))
                .ToArray();

            foreach (var t in TypeDiscovery.FindTypesWithAttribute<WorldObjectAttribute>(assemblies))
            {
                if (!typeof(IWorldObject2D).IsAssignableFrom(t))
                    continue;

                var attr = t.GetCustomAttribute<WorldObjectAttribute>(inherit: false);
                if (attr is null || string.IsNullOrWhiteSpace(attr.Archetype))
                    continue;

                var key = attr.Archetype.Trim();
                if (!map.ContainsKey(key))
                    map[key] = t;
            }

            return map;
        }
    }
}
