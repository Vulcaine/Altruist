/*
Copyright 2025 Aron Gere
Licensed under the Apache License, Version 2.0
*/

using System.Numerics;
using System.Text.Json.Serialization;

namespace Altruist.Gaming.TwoD
{
    // -------------------------------------------------------------------------
    // Raw JSON model (mirrors 2D scene export from editors)
    // -------------------------------------------------------------------------

    /// <summary>Root of a 2D world JSON file read by <see cref="IWorldLoader2D"/>.
    /// <example><code>
    /// {
    ///   "transform": { "position": { "x": 0, "y": 0 }, "rotation": 0, "size": { "x": 200, "y": 100 } },
    ///   "objects": [
    ///     { "archetype": "wall", "position": { "x": 100, "y": 0 }, "size": { "x": 200, "y": 2 },
    ///       "colliders": [ { "shape": "box", "size": { "x": 200, "y": 2 } } ] }
    ///   ]
    /// }
    /// </code></example></summary>
    public sealed class WorldSchema2D
    {
        /// <summary>World root transform: its position / rotation offset every object; its size becomes the world size.</summary>
        [JsonPropertyName("transform")]
        public required WorldTransformSchema2D Transform { get; set; }

        /// <summary>Top-level objects (each may have children).</summary>
        [JsonPropertyName("objects")]
        public required List<WorldObjectSchema2D> Objects { get; set; }
    }

    /// <summary>The world's root transform.</summary>
    public sealed class WorldTransformSchema2D
    {
        /// <summary>World origin, world units.</summary>
        [JsonPropertyName("position")]
        public required Vector2Schema2D Position { get; set; }

        /// <summary>Root rotation in radians, counter-clockwise.</summary>
        [JsonPropertyName("rotation")]
        public float Rotation { get; set; }

        /// <summary>World width / height, world units (truncated to integers).</summary>
        [JsonPropertyName("size")]
        public required Vector2Schema2D Size { get; set; }
    }

    /// <summary>One node of the world hierarchy; position and rotation are relative to the parent.</summary>
    public sealed class WorldObjectSchema2D
    {
        /// <summary>Editor id (informational; not used by the loader).</summary>
        [JsonPropertyName("id")]
        public string Id { get; set; } = "";

        /// <summary>Body kind hint, default <c>Static</c> (informational; the loader spawns every node as static).</summary>
        [JsonPropertyName("type")]
        public string Type { get; set; } = "Static";

        /// <summary>Archetype mapped to a <see cref="WorldObjectAttribute"/> type (case-insensitive); unmatched or
        /// empty values produce an <see cref="AnonymousWorldObject2D"/>.</summary>
        [JsonPropertyName("archetype")]
        public string? Archetype { get; set; }

        /// <summary>Position relative to the parent (rotated by the parent's accumulated rotation), world units.</summary>
        [JsonPropertyName("position")]
        public required Vector2Schema2D Position { get; set; }

        /// <summary>Rotation relative to the parent, radians.</summary>
        [JsonPropertyName("rotation")]
        public float Rotation { get; set; }

        /// <summary>Object size, world units (becomes the object's <c>Transform.Size</c>).</summary>
        [JsonPropertyName("size")]
        public required Vector2Schema2D Size { get; set; }

        /// <summary>Colliders; a node without a recognised collider is not spawned (its children still are).</summary>
        [JsonPropertyName("colliders")]
        public List<WorldColliderSchema2D> Colliders { get; set; } = new();

        /// <summary>Child nodes.</summary>
        [JsonPropertyName("children")]
        public List<WorldObjectSchema2D> Children { get; set; } = new();
    }

    /// <summary>A collider on a <see cref="WorldObjectSchema2D"/> node.</summary>
    public sealed class WorldColliderSchema2D
    {
        /// <summary>"box", "circle", "capsule"</summary>
        [JsonPropertyName("shape")]
        public string Shape { get; set; } = "";

        /// <summary>Box full size (default 1 x 1).</summary>
        [JsonPropertyName("size")]
        public Vector2Schema2D? Size { get; set; }

        /// <summary>Offset from the node's position, world units (not rotated).</summary>
        [JsonPropertyName("center")]
        public Vector2Schema2D? Center { get; set; }

        /// <summary>Circle / capsule radius (default 0.5).</summary>
        [JsonPropertyName("radius")]
        public float? Radius { get; set; }

        /// <summary>Capsule total height (default 1; half of it is the half length).</summary>
        [JsonPropertyName("height")]
        public float? Height { get; set; }
    }

    /// <summary>A JSON 2D vector (<c>{ "x": .., "y": .. }</c>; property names follow the serializer options).</summary>
    public sealed class Vector2Schema2D
    {
        /// <summary>X component.</summary>
        public float X { get; set; }
        /// <summary>Y component.</summary>
        public float Y { get; set; }

        /// <summary>Converts to <see cref="Vector2"/>.</summary>
        public Vector2 ToNumerics() => new(X, Y);
    }
}
