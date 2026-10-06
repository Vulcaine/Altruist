/*
Copyright 2025 Aron Gere
Licensed under the Apache License, Version 2.0
*/

using System.Text.Json.Serialization;

namespace Altruist;

/// <summary>Whether this server takes new work (written by name in JSON).</summary>
[JsonConverter(typeof(JsonStringEnumConverter<ServerNodeState>))]
public enum ServerNodeState
{
    /// <summary>Starting up, or a required service (database, cache) is not connected.</summary>
    Starting,
    /// <summary>Takes new work.</summary>
    Ready,
    /// <summary>Its load reached <c>max-load</c>: running work goes on, nothing new starts.</summary>
    Full,
    /// <summary>Going away: nothing new starts and the running work is let finish.</summary>
    Draining,
    /// <summary>Drained: nothing runs any more (or the drain timed out and the rest was stopped).</summary>
    Drained,
}

/// <summary>One kind of work's share of the server: how many units run and the load they put on it.</summary>
/// <param name="Units">Live units (rooms, worlds, connections, ...).</param>
/// <param name="Load">Their cost in the server's load units (compared with <c>max-load</c>).</param>
public readonly record struct CapacitySample(int Units, double Load)
{
    public static readonly CapacitySample Empty = new(0, 0);
}

/// <summary>
/// Something that runs on this server and counts against its capacity (a room host, a world
/// organizer, ...). Register it as a service (<c>[Service(typeof(ICapacityContributor))]</c>) or
/// at run time with <see cref="IServerNode.Register(ICapacityContributor)"/>.
/// </summary>
public interface ICapacityContributor
{
    /// <summary>The kind of work (<c>rooms</c>, <c>worlds</c>, ...); the capacity report groups by it.</summary>
    string Kind { get; }

    /// <summary>The current sample. Called from any thread (HTTP, metrics): must be thread-safe and cheap.</summary>
    CapacitySample Sample();
}

/// <summary>
/// Something that has to wind down before the server stops: it stops taking new work when the
/// drain begins and reports when it is done. Register it as a service
/// (<c>[Service(typeof(IDrainParticipant))]</c>) or with <see cref="IServerNode.Register(IDrainParticipant)"/>.
/// </summary>
public interface IDrainParticipant
{
    string Kind { get; }

    /// <summary>The drain began: take no new work, let the running work finish. Called once, from any thread.</summary>
    void BeginDrain();

    /// <summary>Nothing runs any more. Read from any thread.</summary>
    bool IsDrained { get; }

    /// <summary>The drain timed out: stop what still runs now. Called once at most, from any thread.</summary>
    void ForceStop();
}

/// <summary>One kind of work in a <see cref="ServerCapacity"/> report.</summary>
public sealed record CapacityByKind(string Kind, int Units, double Load);

/// <summary>A point-in-time capacity report of this server.</summary>
/// <param name="State">Whether it takes new work.</param>
/// <param name="Load">Summed load of every contributor.</param>
/// <param name="MaxLoad">The configured budget (<c>altruist:server:capacity:max-load</c>); 0 = unlimited.</param>
/// <param name="Kinds">Units and load per kind of work.</param>
public sealed record ServerCapacity(string NodeId, ServerNodeState State, double Load, double MaxLoad, IReadOnlyList<CapacityByKind> Kinds)
{
    public bool Unlimited => MaxLoad <= 0;

    /// <summary>Load still available; null when unlimited (JSON has no infinity).</summary>
    public double? Free => Unlimited ? null : Math.Max(0, MaxLoad - Load);

    /// <summary>Load / MaxLoad (0 when unlimited).</summary>
    public double Utilization => Unlimited ? 0 : Load / MaxLoad;

    public bool Accepting => State == ServerNodeState.Ready;

    public int UnitsOf(string kind) => Kinds.FirstOrDefault(k => k.Kind == kind)?.Units ?? 0;
}

/// <summary>
/// This server process as a member of a fleet: its capacity (the sum of its
/// <see cref="ICapacityContributor"/>s against <c>max-load</c>) and its drain (every
/// <see cref="IDrainParticipant"/> winds down before the process stops).
/// <para>
/// Nothing here touches the network or the engine's hot path: allocators, autoscalers and load
/// balancers read it through <c>/altruist/server/*</c> and the <c>Altruist.Server</c> meter.
/// </para>
/// </summary>
public interface IServerNode
{
    /// <summary>The process id (<see cref="IAltruistContext.ProcessId"/>), or a generated one.</summary>
    string NodeId { get; }

    ServerNodeState State { get; }

    /// <summary>The configured load budget; 0 = unlimited.</summary>
    double MaxLoad { get; }

    /// <summary>How long a drain waits for its participants before stopping them.</summary>
    TimeSpan DrainTimeout { get; }

    bool IsDraining { get; }

    ServerCapacity Capacity();

    /// <summary>New work of this cost may start: the server is <see cref="ServerNodeState.Ready"/> and the cost fits the budget.</summary>
    bool CanAccept(double cost = 1);

    /// <summary>Adds a contributor; dispose the result to remove it.</summary>
    IDisposable Register(ICapacityContributor contributor);

    /// <summary>Adds a drain participant (told at once when the drain already began); dispose the result to remove it.</summary>
    IDisposable Register(IDrainParticipant participant);

    /// <summary>The drain began (raised once, on the thread that started it).</summary>
    event Action? DrainStarted;

    /// <summary>
    /// Stops new work and waits until every participant drained, up to <paramref name="timeout"/>
    /// (default <see cref="DrainTimeout"/>); then the rest is stopped. True when everything drained
    /// in time. Every caller shares the same drain.
    /// </summary>
    Task<bool> DrainAsync(TimeSpan? timeout = null, CancellationToken cancellationToken = default);
}
