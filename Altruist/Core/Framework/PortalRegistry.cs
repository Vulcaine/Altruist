/*
Copyright 2025 Aron Gere
Licensed under the Apache License, Version 2.0
*/

using System.Collections.Concurrent;

public static class PortalGateRegistry<TMarker>
{
    private static readonly ConcurrentDictionary<string, List<Delegate>> _handlers =
        new(StringComparer.Ordinal);

    private static readonly ConcurrentBag<TMarker> _instances = new();

    /// <summary>Register a handler delegate for an event name.</summary>
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
    public static void RegisterInstance(TMarker instance)
    {
        if (instance is null)
            throw new ArgumentNullException(nameof(instance));
        _instances.Add(instance);
    }

    /// <summary>Get all handlers for an event. Returns empty if none.</summary>
    public static IReadOnlyList<Delegate> Get(string eventName)
    {
        return _handlers.TryGetValue(eventName, out var list)
            ? list.ToArray()
            : Array.Empty<Delegate>();
    }

    /// <summary>
    /// Back-compat: Try to get a single handler for an event.
    /// If multiple were registered, returns the most recently added.
    /// </summary>
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
    /// </summary>
    public static IReadOnlyList<TMarker> GetAllHandlers()
        => _instances.ToArray();
}
