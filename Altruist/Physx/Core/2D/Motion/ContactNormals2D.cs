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
/// <c>sum / l</c> with <c>l = sum.Length()</c> (1 when zero).</para>
/// <para>Typical source: <c>ContactQueries2D.TouchingContactsOf</c> after a step (its normal points
/// from the other fixture toward the body), or a pre-solve handler. <c>Reset</c> by assigning
/// <c>default</c>.</para>
/// <example><code>
/// var normals = default(ContactNormals2D);
/// foreach (var c in world.TouchingContactsOf&lt;SurfaceTag&gt;(body))
///     normals.Add(c.Normal, body.GetWorldVector(Vector2.UnitY), minUpDot: 0.7f);
/// if (normals.HasSupport) { var ground = normals.SupportDirection; }
/// </code></example></summary>
public struct ContactNormals2D
{
    /// <summary>Sum of every added normal.</summary>
    public Vector2 All;
    /// <summary>True once any normal was added (<see cref="All"/> may still sum to zero).</summary>
    public bool HasAny;
    /// <summary>Sum of the supporting normals.</summary>
    public Vector2 Support;
    /// <summary>True once a supporting normal was added.</summary>
    public bool HasSupport;

    /// <summary>Adds a contact normal; it also counts as support when
    /// <c>Vector2.Dot(normal, up) &gt;= minUpDot</c>.</summary>
    /// <param name="normal">Unit contact normal pointing out of the surface toward the body.</param>
    /// <param name="up">The body's unit "up" (e.g. <c>body.GetWorldVector((0, 1))</c>, or world +Y).</param>
    /// <param name="minUpDot">Cosine of the widest support angle (e.g. 0.7 ≈ 45°).</param>
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
