/*
Copyright 2025 Aron Gere
Licensed under the Apache License, Version 2.0
*/

namespace Altruist.Gaming;

/// <summary>
/// Optional marker for entities whose facing direction is maintained
/// outside of <c>Transform.Rotation</c> (e.g. player body yaw driven by
/// <c>InputIntent.FacingYaw</c>, kept authoritative on the server).
///
/// The lag-compensation recorder queries this interface first when snapshotting
/// an entity's orientation; entities that don't implement it fall back to
/// extracting yaw from <see cref="Altruist.ThreeD.Numerics.Rotation3D"/>.
/// </summary>
public interface IHasFacingYaw
{
    /// <summary>Y-axis rotation in radians (yaw). Same convention as
    /// <c>MathF.Atan2(worldForwardX, worldForwardZ)</c>.</summary>
    float FacingYaw { get; }
}
