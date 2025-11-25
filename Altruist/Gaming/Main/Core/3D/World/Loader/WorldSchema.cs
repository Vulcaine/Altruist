namespace Altruist.Gaming.ThreeD;

using System;
using System.Collections.Generic;
using System.Numerics;
using System.Text.Json.Serialization;

// ------------------------------------------------------------
// RAW JSON model (mirrors client-side Triton.WorldExport.Schemas)
// ------------------------------------------------------------

public sealed class WorldSchema
{
    // Overall world / landscape transform
    [JsonPropertyName("transform")]
    public WorldTransformSchema Transform { get; set; } = new();

    // Root-level objects; each has children to mirror Unity hierarchy
    [JsonPropertyName("objects")]
    public List<WorldObjectSchema> Objects { get; set; } = new();
}

public sealed class WorldTransformSchema
{
    [JsonPropertyName("position")]
    public Vector3Schema Position { get; set; }

    [JsonPropertyName("rotation")]
    public Vector3Schema RotationEuler { get; set; }

    [JsonPropertyName("scale")]
    public Vector3Schema Scale { get; set; }

    [JsonPropertyName("size")]
    public Vector3Schema Size { get; set; }
}

public sealed class WorldObjectSchema
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("type")]
    public string Type { get; set; } = "Static";

    [JsonPropertyName("archetype")]
    public string? Archetype { get; set; }

    [JsonPropertyName("position")]
    public Vector3 Position { get; set; }

    [JsonPropertyName("rotation")]
    public Vector3 RotationEuler { get; set; }

    [JsonPropertyName("scale")]
    public Vector3 Scale { get; set; }

    // Exact world-space size (AABB) of this subtree
    [JsonPropertyName("size")]
    public Vector3? Size { get; set; }

    // All colliders belonging to THIS transform
    [JsonPropertyName("colliders")]
    public List<WorldColliderSchema> Colliders { get; set; } = new();

    // Child objects (hierarchy)
    [JsonPropertyName("children")]
    public List<WorldObjectSchema> Children { get; set; } = new();
}

public sealed class WorldColliderSchema
{
    // "box", "sphere", "capsule", "mesh"
    [JsonPropertyName("shape")]
    public string Shape { get; set; } = "";

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
public sealed class NavMeshSchema
{
    [JsonPropertyName("vertices")]
    public Vector3[] Vertices { get; set; } = [];

    [JsonPropertyName("indices")]
    public int[] Indices { get; set; } = [];
}

public sealed class Vector3Schema
{
    public float X { get; set; }
    public float Y { get; set; }
    public float Z { get; set; }

    public Vector3 ToNumerics() => new Vector3(X, Y, Z);
}
