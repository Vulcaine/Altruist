/*
Copyright 2025 Aron Gere
Licensed under the Apache License, Version 2.0
*/

using System.Collections.Concurrent;

public static class EventHandlerRegistry<TMarker>
{
    private static readonly ConcurrentDictionary<string, List<Delegate>> _handlers =
        new(StringComparer.Ordinal);

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

    /// <summary>Get all handlers for an event. Returns empty if none.</summary>
    public static IReadOnlyList<Delegate> Get(string eventName)
    {
        return _handlers.TryGetValue(eventName, out var list)
            ? list.ToArray()
            : Array.Empty<Delegate>();
    }
}
