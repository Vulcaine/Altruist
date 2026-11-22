namespace Altruist.Gaming.ThreeD;

using System;
using System.Collections.Generic;
using System.Numerics;
using System.Text.Json.Serialization;

// ------------------------------------------------------------
// RAW JSON model (mirrors client-side Triton.WorldExport.Schemas)
// ------------------------------------------------------------

/// <summary>
/// Root of the exported 3D scene JSON consumed by <see cref="IWorldLoader3D.LoadFromJson"/> (and by <c>LoadFromIndex</c> via
/// the world's <c>data-path</c>). Mirrors a Unity-style hierarchy: rotations are Euler degrees, box sizes are full sizes.
/// </summary>
/// <example>
/// <code>
/// {
///   "transform": { "position": {"X":0,"Y":0,"Z":0}, "rotation": {"X":0,"Y":0,"Z":0},
///                  "scale": {"X":1,"Y":1,"Z":1}, "size": {"X":512,"Y":128,"Z":512} },
///   "objects": [
///     { "id": "rock-1", "type": "Static", "archetype": "rock",
///       "position": {"X":10,"Y":0,"Z":4}, "rotation": {"X":0,"Y":45,"Z":0},
///       "scale": {"X":1,"Y":1,"Z":1}, "size": {"X":2,"Y":2,"Z":2},
///       "colliders": [ { "shape": "box", "size": {"X":2,"Y":2,"Z":2} } ] }
///   ]
/// }
/// </code>
/// </example>
public sealed class WorldSchema
{
    // Overall world / landscape transform
    /// <summary>World root transform; its <c>size</c> becomes the world size and its <c>position</c> the world origin.</summary>
    [JsonPropertyName("transform")]
    public required WorldTransformSchema Transform { get; set; }

    // Root-level objects; each has children to mirror Unity hierarchy
    /// <summary>Root-level scene nodes.</summary>
    [JsonPropertyName("objects")]
    public required List<WorldObjectSchema> Objects { get; set; }
}

/// <summary>Transform of the world root (<c>"transform"</c>).</summary>
public sealed class WorldTransformSchema
{
    /// <summary>World origin.</summary>
    [JsonPropertyName("position")]
    public required Vector3Schema Position { get; set; }

    /// <summary>Euler rotation in degrees (JSON <c>"rotation"</c>).</summary>
    [JsonPropertyName("rotation")]
    public required Vector3Schema RotationEuler { get; set; }

    /// <summary>Scale applied to every child.</summary>
    [JsonPropertyName("scale")]
    public required Vector3Schema Scale { get; set; }

    /// <summary>World extent; truncated to integers for <c>IWorldIndex3D.Size</c>.</summary>
    [JsonPropertyName("size")]
    public required Vector3Schema Size { get; set; }
}

/// <summary>One scene node; becomes a world object when it has at least one supported collider. Children inherit its transform.</summary>
public sealed class WorldObjectSchema
{
    /// <summary>Exporter id (informational; not used as the instance id).</summary>
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    /// <summary><c>"Dynamic"</c> (case-insensitive) for a dynamic body; anything else (default <c>"Static"</c>) is static geometry.</summary>
    [JsonPropertyName("type")]
    public string Type { get; set; } = "Static";

    /// <summary>Archetype matched (case-insensitive) against <see cref="WorldObjectAttribute"/> types; unmatched or empty gives an anonymous object.</summary>
    [JsonPropertyName("archetype")]
    public string? Archetype { get; set; }

    /// <summary>Position relative to the parent node.</summary>
    [JsonPropertyName("position")]
    public required Vector3Schema Position { get; set; }

    /// <summary>Local Euler rotation in degrees (JSON <c>"rotation"</c>).</summary>
    [JsonPropertyName("rotation")]
    public required Vector3Schema RotationEuler { get; set; }

    /// <summary>Local scale (multiplied with the parent scale).</summary>
    [JsonPropertyName("scale")]
    public required Vector3Schema Scale { get; set; }

    // Exact world-space size (AABB) of this subtree
    /// <summary>World-space AABB size of this subtree; used as the object's <c>Transform.Size</c>.</summary>
    [JsonPropertyName("size")]
    public required Vector3Schema Size { get; set; }

    // All colliders belonging to THIS transform
    /// <summary>Colliders on this node (unsupported shapes are skipped).</summary>
    [JsonPropertyName("colliders")]
    public List<WorldColliderSchema> Colliders { get; set; } = new();

    // Child objects (hierarchy)
    /// <summary>Child nodes.</summary>
    [JsonPropertyName("children")]
    public List<WorldObjectSchema> Children { get; set; } = new();
}

/// <summary>A collider on a scene node, in the node's local space.</summary>
public sealed class WorldColliderSchema
{
    // "box", "sphere", "capsule", "mesh"
    /// <summary><c>"box"</c>, <c>"mesh"</c> (approximated by its bounds box), <c>"sphere"</c> or <c>"capsule"</c> (case-insensitive).</summary>
    [JsonPropertyName("shape")]
    public string Shape { get; set; } = "";

    /// <summary>Box full size in local units (default 1,1,1).</summary>
    [JsonPropertyName("size")]
    public Vector3Schema? Size { get; set; }

    /// <summary>Local center offset (default 0).</summary>
    [JsonPropertyName("center")]
    public Vector3Schema? Center { get; set; }

    /// <summary>Sphere/capsule radius in local units (default 0.5).</summary>
    [JsonPropertyName("radius")]
    public float? Radius { get; set; }

    /// <summary>Capsule total height in local units (default 1).</summary>
    [JsonPropertyName("height")]
    public float? Height { get; set; }

    /// <summary>Capsule axis: 0 = X, 1 = Y (default), 2 = Z.</summary>
    [JsonPropertyName("direction")]
    public int? Direction { get; set; }
}

/// <summary>Raw navmesh JSON (<c>"vertices"</c>, <c>"indices"</c>) read by <see cref="NavMeshLoader"/>.</summary>
[Serializable]
public sealed class NavMeshSchema
{
    /// <summary>Vertex positions in world space.</summary>
    [JsonPropertyName("vertices")]
    public Vector3Schema[] Vertices { get; set; } = [];

    /// <summary>Triangle list: three vertex indices per triangle.</summary>
    [JsonPropertyName("indices")]
    public int[] Indices { get; set; } = [];
}

/// <summary>JSON vector with <c>X</c>, <c>Y</c>, <c>Z</c> properties.</summary>
public sealed class Vector3Schema
{
    /// <summary>X component.</summary>
    public float X { get; set; }
    /// <summary>Y component.</summary>
    public float Y { get; set; }
    /// <summary>Z component.</summary>
    public float Z { get; set; }

    /// <summary>Converts to <see cref="Vector3"/>.</summary>
    public Vector3 ToNumerics() => new Vector3(X, Y, Z);
}
