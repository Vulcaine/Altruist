namespace Altruist.Gaming;

/// <summary>How <see cref="ILagCompensationService.RewindWorld(long, Action)"/> picks an entity's state at a
/// tick (<c>altruist:game:lag-compensation:snapshot-strategy</c>).</summary>
public enum LagCompensationSnapshotStrategy
{
    /// <summary>The recorded snapshot closest to the tick (default; cheapest).</summary>
    Nearest,
    /// <summary>Linear interpolation between the surrounding snapshots (smoother when snapshots are sparse).</summary>
    Interpolate,
}
