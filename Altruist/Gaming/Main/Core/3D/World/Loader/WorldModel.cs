namespace Altruist.Gaming.World.ThreeD;

using System.Numerics;
using System.Text.Json.Serialization;

using Altruist.Physx.ThreeD;

public sealed class WorldStaticData
{
    public List<WorldStaticObjectData> Objects { get; } = new();
    public List<WorldStaticObjectData> CollisionObjects { get; } = new();
}

public sealed class WorldStaticObjectData
{
    public string Id { get; init; } = "";
    public string Type { get; init; } = "Static";

    public Vector3 Position { get; init; }
    public Quaternion Rotation { get; init; }
    public Vector3 Scale { get; init; }

    public WorldStaticColliderData? Collider { get; init; }
}

public sealed class WorldStaticColliderData
{
    public PhysxColliderShape3D Shape { get; init; }
    public Vector3 Center { get; init; }

    // Box
    public Vector3? Size { get; init; }

    // Sphere
    public float? Radius { get; init; }

    // Capsule
    public float? Height { get; init; }
    public int? Direction { get; init; }
}



public sealed class WorldRaw
{
    [JsonPropertyName("objects")]
    public List<WorldObjectData> Objects { get; set; } = new();
}

public sealed class WorldObjectData
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("type")]
    public string Type { get; set; } = "Static";

    [JsonPropertyName("pos")]
    public Vector3 Position { get; set; }

    [JsonPropertyName("rot")]
    public Vector3 RotationEuler { get; set; }

    [JsonPropertyName("scale")]
    public Vector3 Scale { get; set; }

    [JsonPropertyName("collider")]
    public WorldColliderData? Collider { get; set; }
}

public sealed class WorldColliderData
{
    [JsonPropertyName("shape")]
    public string Shape { get; set; } = "";  // "box", "sphere", "capsule"

    [JsonPropertyName("size")]
    public Vector3? Size { get; set; }

    [JsonPropertyName("center")]
    public Vector3? Center { get; set; }

    [JsonPropertyName("radius")]
    public float? Radius { get; set; }

    [JsonPropertyName("height")]
    public float? Height { get; set; }

    [JsonPropertyName("direction")]
    public int? Direction { get; set; }
}

[Serializable]
public class NavMeshData
{
    public Vector3[] Vertices = [];
    public int[] Indices = [];
}
