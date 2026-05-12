/*
Copyright 2025 Aron Gere
Licensed under the Apache License, Version 2.0
*/

using System.Numerics;

namespace Altruist.ThreeD.Numerics;

/// <summary>"Build a rotation that…" — quaternion construction from yaw,
/// axis-angle, or full Euler. Companion to <see cref="Yaw"/> (which
/// extracts) and <see cref="Rotation3D"/> (which wraps with a domain type).</summary>
public static class Rotation
{
    /// <summary>Y-axis rotation by <paramref name="yawRadians"/>. The most
    /// common need — a body that should face a yaw.</summary>
    public static Quaternion Calculate(float yawRadians)
        => Quaternion.CreateFromAxisAngle(Vector3.UnitY, yawRadians);

    public static Quaternion FromAxisAngle(Vector3 axis, float radians)
        => Quaternion.CreateFromAxisAngle(Vector3.Normalize(axis), radians);

    public static Quaternion FromEuler(float yaw, float pitch, float roll)
        => Quaternion.CreateFromYawPitchRoll(yaw, pitch, roll);
}
