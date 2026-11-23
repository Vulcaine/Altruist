/*
Copyright 2025 Aron Gere
Licensed under the Apache License, Version 2.0
*/

namespace Altruist.Gaming;

/// <summary>
/// The one source of <see cref="ThreeD.IWorldObject3D.VirtualId"/> values in a process: the world manager draws from it when
/// it spawns an object whose id is still 0, and game code draws from it for ids it needs before a spawn (a player's id
/// sent at login, a ground item announced before it enters the world). Ids from separate counters collide on the wire.
/// </summary>
public interface IVirtualIdAllocator
{
    /// <summary>The next id: never 0, never handed out twice in this process.</summary>
    uint Allocate();
}

/// <summary>Sequential <see cref="IVirtualIdAllocator"/> starting at 1. Thread-safe.</summary>
[Service(typeof(IVirtualIdAllocator))]
public sealed class VirtualIdAllocator : IVirtualIdAllocator
{
    private uint _last;

    /// <inheritdoc/>
    public uint Allocate() => Interlocked.Increment(ref _last);
}
