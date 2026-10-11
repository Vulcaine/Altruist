/*
Copyright 2025 Aron Gere
Licensed under the Apache License, Version 2.0
*/

using System.Collections.Concurrent;

/// <summary>
/// Process-wide static registry of gate handlers (packet event name to delegate) and live portal instances,
/// keyed by a marker type. The framework uses <c>PortalGateRegistry&lt;IPortal&gt;</c>: at startup every
/// <see cref="Altruist.GateAttribute"/> method is registered here and every portal instance is recorded for
/// connection lifecycle hooks; the connection manager, rate limiter and dashboard read it at runtime.
/// </summary>
/// <remarks>
/// Application code normally never calls this; declare handlers with <see cref="Altruist.PortalAttribute"/> and
/// <see cref="Altruist.GateAttribute"/> instead. Calling <see cref="Register"/> directly is useful in tests or for
/// handlers created at runtime. State is static per <typeparamref name="TMarker"/> and lives for the process
/// (there is no unregister). Registration and reads are thread-safe.
/// </remarks>
/// <typeparam name="TMarker">Marker type that partitions the registry; also the type of recorded instances.</typeparam>
public static class PortalGateRegistry<TMarker> where TMarker : notnull
{
    private static readonly ConcurrentDictionary<string, List<Delegate>> _handlers =
        new(StringComparer.Ordinal);

    private static readonly ConcurrentDictionary<TMarker, byte> _instances = new();

    /// <summary>Register a handler delegate for an event name.</summary>
    /// <remarks>
    /// Handlers accumulate per event; packet dispatch uses <see cref="TryGetHandler"/>, i.e. the most recently
    /// registered one. The delegate must have a gate-compatible shape (<c>()</c>, <c>(string clientId)</c> or
    /// <c>(TPacket, string clientId)</c> returning <see cref="Task"/>); this method does not validate it.
    /// </remarks>
    /// <param name="eventName">Packet event name (ordinal, case-sensitive).</param>
    /// <param name="handler">Handler delegate.</param>
    /// <exception cref="ArgumentException"><paramref name="eventName"/> is null or whitespace.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="handler"/> is null.</exception>
    public static void Register(string eventName, Delegate handler)
    {
        if (string.IsNullOrWhiteSpace(eventName))
            throw new ArgumentException("eventName cannot be null/empty.", nameof(eventName));
        if (handler is null)
            throw new ArgumentNullException(nameof(handler));

        var list = _handlers.GetOrAdd(eventName, _ => new List<Delegate>());
        lock (list)
        { list.Add(handler); }
    }

    /// <summary>Register a marker instance (e.g., a portal) for lifecycle notifications.</summary>
    /// <param name="instance">Instance to record; registering the same instance twice is a no-op.</param>
    /// <exception cref="ArgumentNullException"><paramref name="instance"/> is null.</exception>
    public static void RegisterInstance(TMarker instance)
    {
        if (instance is null)
            throw new ArgumentNullException(nameof(instance));


        _instances.TryAdd(instance, 0);
    }

    /// <summary>Get all handlers for an event. Returns empty if none.</summary>
    /// <param name="eventName">Packet event name.</param>
    /// <returns>A snapshot copy in registration order.</returns>
    public static IReadOnlyList<Delegate> Get(string eventName)
    {
        return _handlers.TryGetValue(eventName, out var list)
            ? list.ToArray()
            : Array.Empty<Delegate>();
    }

    /// <summary>
    /// Snapshot of every registered (event name, handler) pair across all events, e.g. for listing gates in
    /// tooling. Duplicate handlers for one event all appear. Order across events is unspecified.
    /// </summary>
    public static IReadOnlyList<(string EventName, Delegate Handler)> GetAllHandlerEntries()
    {
        var entries = new List<(string EventName, Delegate Handler)>();
        foreach (var (eventName, list) in _handlers)
        {
            lock (list)
            {
                foreach (var handler in list)
                    entries.Add((eventName, handler));
            }
        }

        return entries;
    }

    /// <summary>
    /// Back-compat: Try to get a single handler for an event.
    /// If multiple were registered, returns the most recently added.
    /// This is what packet dispatch uses; for every handler use <see cref="Get"/>.
    /// </summary>
    /// <param name="eventName">Packet event name.</param>
    /// <param name="handler">The last-registered handler, or <c>null</c> when this returns false.</param>
    /// <returns>True when at least one handler is registered for the event.</returns>
    public static bool TryGetHandler(string eventName, out Delegate handler)
    {
        handler = default!;
        if (!_handlers.TryGetValue(eventName, out var list) || list.Count == 0)
            return false;

        lock (list)
        {
            handler = list[^1]; // last registered wins
        }
        return true;
    }

    /// <summary>
    /// Back-compat: Return all registered marker instances (e.g., IPortal implementations).
    /// Despite the name, these are the instances from <see cref="RegisterInstance"/>, not gate handlers
    /// (use <see cref="GetAllHandlerEntries"/> for those). Returns a snapshot.
    /// </summary>
    public static IReadOnlyList<TMarker> GetAllHandlers()
        => _instances.Keys.ToArray();
}
