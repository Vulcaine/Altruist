/*
Copyright 2025 Aron Gere
Licensed under the Apache License, Version 2.0
*/

using Altruist.Gaming.ThreeD;

namespace Altruist.Gaming;

/// <summary>
/// Records entity position snapshots each engine tick for temporal queries.
/// Implemented by the lag compensation module (Altruist.Gaming.Combat).
/// The world organizer calls RecordSnapshot() after physics/movement each tick
/// and passes the current entity set — a one-way push avoids a circular DI
/// dependency between the Organizer and the Recorder.
/// </summary>
public interface IPositionHistoryRecorder
{
    void RecordSnapshot(long tick, IEnumerable<IWorldObject3D> entities);
}
