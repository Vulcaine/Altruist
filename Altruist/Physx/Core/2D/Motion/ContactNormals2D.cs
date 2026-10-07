/*
Copyright 2025 Aron Gere
Licensed under the Apache License, Version 2.0
*/

using System.Numerics;

namespace Altruist.Physx.TwoD;

/// <summary>Physics layer. Sums the contact normals of one body over a step and tells supporting
/// contacts (ground: the normal within a cone of the body's "up") from the rest (walls, ceilings).
/// Feed it each touching contact's normal pointing out of the surface toward the body; read the
/// averaged directions afterwards. A value type: <c>default</c> is empty.
/// <para>Deterministic: normals are summed in the order they are added (vector addition), the
/// support test is <c>Vector2.Dot(normal, up) &gt;= minUpDot</c> and the directions are
/// <c>sum / l</c> with <c>l = sum.Length()</c> (1 when zero).</para></summary>
public struct ContactNormals2D
{
    /// <summary>Sum of every added normal.</summary>
    public Vector2 All;
    public bool HasAny;
    /// <summary>Sum of the supporting normals.</summary>
    public Vector2 Support;
    public bool HasSupport;

    /// <summary>Adds a contact normal; it also counts as support when
    /// <c>Vector2.Dot(normal, up) &gt;= minUpDot</c>.</summary>
    public void Add(Vector2 normal, Vector2 up, float minUpDot)
    {
        HasAny = true;
        All += normal;
        if (Vector2.Dot(normal, up) >= minUpDot)
        {
            HasSupport = true;
            Support += normal;
        }
    }

    /// <summary>Adds a contact normal that is not tested for support.</summary>
    public void Add(Vector2 normal)
    {
        HasAny = true;
        All += normal;
    }

    /// <summary>Average direction of all normals (zero when none).</summary>
    public readonly Vector2 AllDirection => Direction(All);

    /// <summary>Average direction of the supporting normals (zero when none).</summary>
    public readonly Vector2 SupportDirection => Direction(Support);

    private static Vector2 Direction(Vector2 sum)
    {
        var l = sum.Length();
        if (l == 0) l = 1;
        return new Vector2(sum.X / l, sum.Y / l);
    }
}
