
using System.Numerics;

namespace Altruist.Physx;

/// <summary>Small helpers for the legacy <see cref="IMovementTypePhysx{TInput}"/> movement models.</summary>
public class MovementHelper
{
    /// <summary>
    /// Unit vector <c>(cos rotation, sin rotation)</c>: math/physics convention, 0 = +X, counter-clockwise positive.
    /// Computed in double precision (<see cref="Math.Cos(double)"/>) and narrowed to float.
    /// </summary>
    /// <remarks>
    /// This is not the facing-yaw convention (0 = +Y) used by <c>Yaw2D</c>/<c>Direction2D.TowardAngle</c>; for polar unit
    /// vectors in this convention prefer <c>Direction2D.FromPolar</c> (float math).
    /// </remarks>
    /// <param name="rotation">Angle in radians.</param>
    public static Vector2 GetDirectionVector(float rotation) => new Vector2((float)Math.Cos(rotation), (float)Math.Sin(rotation));
}
