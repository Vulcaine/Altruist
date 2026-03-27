/*
Copyright 2025 Aron Gere
Licensed under the Apache License, Version 2.0
*/

namespace Altruist;

/// <summary>
/// Marker interface for packets that carry a client-perceived tick for lag compensation.
/// When the framework detects a decoded packet implementing this interface,
/// it automatically sets PacketContext.ClientTick, enabling CombatService
/// to rewind entity positions to the client's perceived time.
///
/// Usage:
///   [MessagePackObject]
///   public class AttackPacket : IPacketBase, ILagCompensated
///   {
///       [Key(0)] public uint MessageCode { get; set; }
///       [Key(1)] public uint TargetVID { get; set; }
///       [Key(2)] public long ClientTick { get; set; }
///   }
/// </summary>
public interface ILagCompensated
{
    /// <summary>
    /// The engine tick as perceived by the client at the time of the action.
    /// 0 = no compensation (use current server tick).
    /// </summary>
    long ClientTick { get; set; }
}
