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

    /// <summary>
    /// Raw bytes of the current packet being processed.
    /// Only available within a Gate handler invocation.
    /// </summary>
    public static byte[]? RawData => _currentRawData.Value;

    internal static void Set(byte[]? data) => _currentRawData.Value = data;
    internal static void Clear() => _currentRawData.Value = null;
}
