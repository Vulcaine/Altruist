using System.Numerics;
using System.Text.Json;

using Altruist.Physx.Contracts;
using Altruist.Physx.ThreeD;
using Altruist.ThreeD.Numerics;

namespace Altruist.Gaming.World.ThreeD;

/// <summary>
/// Loads a hierarchical WorldSchema (exported from Unity) into a 3D physics world,
/// using only Physx abstractions (no BEPU types).
/// </summary>
public sealed class WorldLoader3D
{
    private readonly IPhysxWorldEngineFactory3D _engineFactory;
    private readonly IPhysxBodyApiProvider3D _bodyApi;

    /// <summary>
    /// Takes a collider factory that knows how to turn a WorldColliderSchema + Transform3D
    /// into an engine-specific IPhysxCollider3D.
    /// </summary>
    private readonly Func<WorldColliderSchema, Transform3D, IPhysxCollider3D?> _colliderFactory;

    public WorldLoader3D(
        IPhysxWorldEngineFactory3D engineFactory,
        IPhysxBodyApiProvider3D bodyApi,
        Func<WorldColliderSchema, Transform3D, IPhysxCollider3D?> colliderFactory)
    {
        _engineFactory = engineFactory ?? throw new ArgumentNullException(nameof(engineFactory));
        _bodyApi = bodyApi ?? throw new ArgumentNullException(nameof(bodyApi));
        _colliderFactory = colliderFactory ?? throw new ArgumentNullException(nameof(colliderFactory));
    }

    /// <summary>
    /// Load a physics world from a JSON string that contains a WorldSchema.
    /// </summary>
    public IPhysxWorld3D LoadFromJson(string json, Vector3 gravity, float fixedDeltaTime = 1f / 60f)
    {
        if (string.IsNullOrWhiteSpace(json))
            throw new ArgumentException("World JSON content cannot be null or empty.", nameof(json));

        var options = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        };

        var worldSchema = JsonSerializer.Deserialize<WorldSchema>(json, options)
                          ?? throw new InvalidOperationException("Failed to deserialize world JSON into WorldSchema.");

        return Load(worldSchema, gravity, fixedDeltaTime);
    }

    /// <summary>
    /// Load a physics world from a JSON file path that contains a WorldSchema.
    /// </summary>
    public IPhysxWorld3D LoadFromPath(string path, Vector3 gravity, float fixedDeltaTime = 1f / 60f)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("World JSON path cannot be null or empty.", nameof(path));

        if (!File.Exists(path))
            throw new FileNotFoundException("World JSON file not found.", path);

        var json = File.ReadAllText(path);
        return LoadFromJson(json, gravity, fixedDeltaTime);
    }


    /// <summary>
    /// Creates a physics world from the serialized world schema.
    /// </summary>
    /// <param name="worldSchema">Deserialized world JSON.</param>
    /// <param name="gravity">Gravity vector for this world.</param>
    /// <param name="fixedDeltaTime">Simulation fixed timestep.</param>
    public IPhysxWorld3D Load(WorldSchema worldSchema, Vector3 gravity, float fixedDeltaTime = 1f / 60f)
    {
        if (worldSchema is null)
            throw new ArgumentNullException(nameof(worldSchema));

        // 1) Create engine and wrap in PhysxWorld3D
        var engine = _engineFactory.Create(gravity, fixedDeltaTime);
        var world = new PhysxWorld3D(engine);

        // 2) Root "world" transform from exported landscape
        var worldRoot = new AccumulatedTransform(
            worldSchema.Transform.Position,
            EulerToQuaternion(worldSchema.Transform.RotationEuler),
            worldSchema.Transform.Scale
        );

        // 3) Recursively build bodies from all root objects
        foreach (var rootObj in worldSchema.Objects)
        {
            BuildBodiesRecursive(rootObj, worldRoot, world);
        }

        return world;
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

        var worldRot = Quaternion.Normalize(localRot * parent.Rotation);

        var worldTransform = new AccumulatedTransform(worldPos, worldRot, worldScale);

        if (node.Colliders is { Count: > 0 })
        {
            int colliderIndex = 0;
            foreach (var colliderSchema in node.Colliders)
            {
                var colliderTransform = BuildColliderTransform(worldTransform, colliderSchema);

                var collider = _colliderFactory(colliderSchema, colliderTransform);
                if (collider == null)
                    continue;

                var body = _bodyApi.CreateBody(
                    PhysxBodyType.Static,
                    mass: 0f,
                    transform: colliderTransform
                );

                body.UserData = node.Id;
                _bodyApi.AddCollider(body, collider);
                world.AddBody(body);

                colliderIndex++;
            }
        }

        if (node.Children is { Count: > 0 })
        {
            foreach (var child in node.Children)
            {
                BuildBodiesRecursive(child, worldTransform, world);
            }
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

        // Decide collider size encoding according to your Physx conventions:
        // - Box: Transform3D.Size = half extents
        // - Sphere: Size.X = radius
        // - Capsule: Size.X = radius, Size.Y = half length

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

        var position3D = new Position3D(colliderPositionWorld.X, colliderPositionWorld.Y, colliderPositionWorld.Z);
        var rotation3D = Rotation3D.FromQuaternion(objectWorld.Rotation);
        var scale3D = Scale3D.One;

        return new Transform3D(position3D, size, scale3D, rotation3D);
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
