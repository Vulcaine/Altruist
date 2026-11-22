/*
Copyright 2025 Aron Gere
Licensed under the Apache License, Version 2.0
*/

using Altruist.Gaming.ThreeD;

namespace Altruist.Gaming;

/// <summary>
/// Records entity position snapshots each engine tick for temporal queries.
/// Implemented by <see cref="LagCompensationService"/> (registered when
/// <c>altruist:game:lag-compensation</c> is configured; see <see cref="ILagCompensationService"/>
/// for the rewind side). The 3D world organizer (<see cref="GameWorldOrganizer3D"/>) calls
/// <see cref="RecordSnapshot"/> after physics/movement each tick and passes the current entity
/// set — a one-way push avoids a circular DI dependency between the Organizer and the Recorder.
/// Implement it yourself only to record history for something other than lag compensation
/// (replays, anti-cheat); consumers should inject <see cref="ILagCompensationService"/>.
/// </summary>
public interface IPositionHistoryRecorder
{
    /// <summary>Records the positions of <paramref name="entities"/> for <paramref name="tick"/>. Called on the engine thread once per tick; enumerate the entities right away (do not keep the sequence).</summary>
    /// <param name="tick">The engine tick (<c>AltruistEngine.CurrentTick</c>).</param>
    /// <param name="entities">Every object of every 3D world this tick.</param>
    void RecordSnapshot(long tick, IEnumerable<IWorldObject3D> entities);
}
