/*
Copyright 2025 Aron Gere
Licensed under the Apache License, Version 2.0
*/

namespace Altruist;

/// <summary>
/// Provides access to the raw packet bytes for the current Gate handler invocation.
/// Useful for protocols with variable-length data (e.g. chat messages)
/// where the Gate handler receives a fixed-size struct but needs access
/// to the trailing bytes.
/// </summary>
public static class PacketContext
{
    private static readonly AsyncLocal<byte[]?> _currentRawData = new();
    private static readonly AsyncLocal<long> _clientTick = new();

    /// <summary>
    /// Raw bytes of the current packet being processed.
    /// Only available within a Gate handler invocation.
    /// </summary>
    public static byte[]? RawData => _currentRawData.Value;

    /// <summary>
    /// The client's perceived engine tick at the time the packet was sent.
    /// Set automatically by the framework for packets implementing ILagCompensated.
    /// 0 = no lag compensation (use current server tick).
    /// </summary>
    public static long ClientTick => _clientTick.Value;

    internal static void Set(byte[]? data) => _currentRawData.Value = data;

    /// <summary>
    /// Set the client tick for lag compensation. Called automatically by the framework
    /// when a decoded packet implements ILagCompensated, or manually by custom framers.
    /// </summary>
    public static void SetClientTick(long tick) => _clientTick.Value = tick;

    internal static void Clear()
    {
        _currentRawData.Value = null;
        _clientTick.Value = 0;
    }
}
