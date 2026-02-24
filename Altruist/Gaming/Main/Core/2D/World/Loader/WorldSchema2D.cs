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

    public sealed class WorldSchema2D
    {
        [JsonPropertyName("transform")]
        public required WorldTransformSchema2D Transform { get; set; }

        [JsonPropertyName("objects")]
        public required List<WorldObjectSchema2D> Objects { get; set; }
    }

    public sealed class WorldTransformSchema2D
    {
        [JsonPropertyName("position")]
        public required Vector2Schema2D Position { get; set; }

        [JsonPropertyName("rotation")]
        public float Rotation { get; set; }

        [JsonPropertyName("size")]
        public required Vector2Schema2D Size { get; set; }
    }

    public sealed class WorldObjectSchema2D
    {
        [JsonPropertyName("id")]
        public string Id { get; set; } = "";

        [JsonPropertyName("type")]
        public string Type { get; set; } = "Static";

        [JsonPropertyName("archetype")]
        public string? Archetype { get; set; }

        [JsonPropertyName("position")]
        public required Vector2Schema2D Position { get; set; }

        [JsonPropertyName("rotation")]
        public float Rotation { get; set; }

        [JsonPropertyName("size")]
        public required Vector2Schema2D Size { get; set; }

        [JsonPropertyName("colliders")]
        public List<WorldColliderSchema2D> Colliders { get; set; } = new();

        [JsonPropertyName("children")]
        public List<WorldObjectSchema2D> Children { get; set; } = new();
    }

    public sealed class WorldColliderSchema2D
    {
        /// <summary>"box", "circle", "capsule"</summary>
        [JsonPropertyName("shape")]
        public string Shape { get; set; } = "";

        [JsonPropertyName("size")]
        public Vector2Schema2D? Size { get; set; }

        [JsonPropertyName("center")]
        public Vector2Schema2D? Center { get; set; }

        [JsonPropertyName("radius")]
        public float? Radius { get; set; }

        [JsonPropertyName("height")]
        public float? Height { get; set; }
    }

    public sealed class Vector2Schema2D
    {
        public float X { get; set; }
        public float Y { get; set; }

        public Vector2 ToNumerics() => new(X, Y);
    }
}
