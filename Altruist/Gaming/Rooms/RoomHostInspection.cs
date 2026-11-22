/*
Copyright 2025 Aron Gere

Licensed under the Apache License, Version 2.0 (the "License");
You may not use this file except in compliance with the License.
You may obtain a copy at http://www.apache.org/licenses/LICENSE-2.0
*/

namespace Altruist.Gaming.Rooms;

/// <summary>
/// What an operator sees of a <see cref="RoomHost{TSim,TInput,TPlayer}"/> whatever its game types (the dashboard reads
/// it): its rooms with seats and players, its sessions, and what its modules report. Reports are taken on the engine
/// thread, where the host's state lives, so they complete at the next frame.
/// </summary>
public interface IRoomHostInspection
{
    /// <summary>The game's name (its <c>IRoomGame</c> type).</summary>
    string GameName { get; }

    /// <summary>A report of the host as of the next frame.</summary>
    Task<RoomHostReport> ReportAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Closes a room at the next frame as a forced close (the game's <c>OnForcedClose</c>, then the players go back to the
    /// menu or their lobby). False when no room has that id.
    /// </summary>
    Task<bool> CloseRoomAsync(string roomId, CancellationToken cancellationToken = default);
}

/// <summary>
/// A room-host module's own part of the host report (a queue, the lobbies …): facts and tables the dashboard shows as
/// they are. Called on the engine thread.
/// </summary>
public interface IRoomModuleInspection
{
    /// <summary>The module's report.</summary>
    RoomModuleReport Report(double now);
}

/// <summary>A host report.</summary>
/// <param name="Game">The game's name.</param>
/// <param name="FixedHz">Simulation steps per second.</param>
/// <param name="Step">Steps run.</param>
/// <param name="Sessions">Connected sessions.</param>
/// <param name="Draining">Whether the host drains (no new rooms).</param>
/// <param name="Rooms">Every room.</param>
/// <param name="Modules">What the modules report.</param>
public sealed record RoomHostReport(
    string Game,
    int FixedHz,
    long Step,
    int Sessions,
    bool Draining,
    IReadOnlyList<RoomReport> Rooms,
    IReadOnlyList<RoomModuleReport> Modules);

/// <summary>One room.</summary>
public sealed record RoomReport(
    string Id,
    string Mode,
    string? Playlist,
    string? LobbyCode,
    DateTime StartedAtUtc,
    double Load,
    bool Ended,
    bool Finished,
    float EndedSeconds,
    IReadOnlyList<SeatReport> Seats,
    IReadOnlyList<ParticipantReport> Participants);

/// <summary>One seat: who holds it.</summary>
/// <param name="Id">Seat id.</param>
/// <param name="Team">Team.</param>
/// <param name="Owner">The principal in it, or null.</param>
/// <param name="Bot">The bot driving it (its type), or null.</param>
/// <param name="OpenSeconds">How long it has been open, when open.</param>
/// <param name="Pending">Whether a player is about to take it.</param>
public sealed record SeatReport(int Id, int Team, string? Owner, string? Bot, double? OpenSeconds, bool Pending);

/// <summary>One player of a room and how their input arrives.</summary>
public sealed record ParticipantReport(
    string PrincipalId,
    string? ClientId,
    int Team,
    int? Seat,
    bool Connected,
    double? DisconnectedSeconds,
    bool LeftEarly,
    bool Abandoned,
    InputReport Input);

/// <summary>A player's input buffer.</summary>
/// <param name="Depth">Inputs queued.</param>
/// <param name="TargetDepth">Inputs the buffer aims to hold (jitter cover).</param>
/// <param name="Jitter">Measured arrival jitter, in steps.</param>
/// <param name="Speculating">Steps run on guessed input, not yet confirmed.</param>
/// <param name="Applied">Inputs applied.</param>
/// <param name="Starved">Steps with no input queued.</param>
/// <param name="Stale">Inputs that came too late.</param>
/// <param name="Dropped">Inputs dropped (queue full).</param>
public sealed record InputReport(int Depth, int TargetDepth, double Jitter, int Speculating, long Applied, long Starved, long Stale, long Dropped);

/// <summary>A module's part of a host report.</summary>
/// <param name="Name">The module's name.</param>
/// <param name="Facts">Label and value pairs.</param>
/// <param name="Tables">Tables (title, columns, rows of cells).</param>
public sealed record RoomModuleReport(string Name, IReadOnlyList<KeyValuePair<string, string>> Facts, IReadOnlyList<ReportTable> Tables);

/// <summary>A table in a module report.</summary>
public sealed record ReportTable(string Title, IReadOnlyList<string> Columns, IReadOnlyList<IReadOnlyList<string>> Rows);
