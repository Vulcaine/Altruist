/*
Copyright 2025 Aron Gere
Licensed under the Apache License, Version 2.0
*/

using System.Numerics;

namespace Altruist.Gaming.TwoD
{
    /// <summary>An axis-aligned 2D bounding box given by its min and max corners (world units, +Y up).
    /// A plain value for bounds bookkeeping; for physics-layer box math and containment tests use
    /// <c>Aabb2D</c> from the physics package instead.</summary>
    /// <param name="Min">The lower-left corner (smallest X and Y).</param>
    /// <param name="Max">The upper-right corner (largest X and Y).</param>
    public readonly record struct Bounds2D(Vector2 Min, Vector2 Max)
    {
        /// <summary>Width and height: <c>Max - Min</c>.</summary>
        public Vector2 Size => Max - Min;
        /// <summary>Midpoint: <c>(Min + Max) * 0.5</c>.</summary>
        public Vector2 Center => (Min + Max) * 0.5f;
        /// <summary>Builds the bounds centered on <paramref name="center"/> with full extents <paramref name="size"/>.</summary>
        /// <param name="center">Center point.</param>
        /// <param name="size">Full width and height (not half extents).</param>
        public static Bounds2D FromCenterSize(Vector2 center, Vector2 size)
        {
            var half = size * 0.5f;
            return new(center - half, center + half);
        }
    }
}
