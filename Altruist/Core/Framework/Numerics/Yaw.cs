/*
Copyright 2025 Aron Gere
Licensed under the Apache License, Version 2.0
*/

using System.Numerics;

namespace Altruist.ThreeD.Numerics;

/// <summary>"Which way is this entity facing?" — yaw in radians, single
/// canonical convention (<c>Atan2(worldForwardX, worldForwardZ)</c>) that
/// matches <see cref="Altruist.Gaming.IHasFacingYaw"/>.</summary>
public static class Yaw3D
{
    /// <summary>Extract yaw from a body's <see cref="Quaternion"/>.</summary>
    public static float Calculate(Quaternion rotation)
    {
        // Forward = q * (0,0,1). Project onto XZ plane and take atan2.
        float fx = 2f * (rotation.X * rotation.Z + rotation.W * rotation.Y);
        float fz = 1f - 2f * (rotation.X * rotation.X + rotation.Y * rotation.Y);
        return MathF.Atan2(fx, fz);
    }

    /// <summary>Yaw of the direction (dx, dz) in the XZ plane.</summary>
    public static float FromDirection(float dx, float dz) => MathF.Atan2(dx, dz);

    /// <summary>Yaw that points from <paramref name="from"/> at
    /// <paramref name="to"/> in the XZ plane.</summary>
    public static float FromDirection(Position3D from, Position3D to)
        => MathF.Atan2(to.X - from.X, to.Z - from.Z);

    /// <summary>Yaw of an XZ direction vector. Y component is ignored.</summary>
    public static float FromVector(Vector3 dirXZ) => MathF.Atan2(dirXZ.X, dirXZ.Z);

    /// <summary>Convert a yaw to its corresponding XZ unit forward vector
    /// <c>(sin yaw, 0, cos yaw)</c>.</summary>
    public static Vector3 ToDirection(float yawRadians)
        => new(MathF.Sin(yawRadians), 0f, MathF.Cos(yawRadians));
}
