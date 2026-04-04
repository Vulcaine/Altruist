using Altruist;
using MessagePack;

namespace CombatArena3D;

public static class Codes
{
    public const uint Join = 100;
    public const uint JoinOk = 101;
    public const uint Move = 110;
    public const uint Attack = 120;
    public const uint AoeAttack = 121;
}

[MessagePackObject]
public class CJoin : IPacketBase
{
    [Key(0)] public uint MessageCode { get; set; } = Codes.Join;
    [Key(1)] public string Name { get; set; } = "";
}

[MessagePackObject]
public class SJoinOk : IPacketBase
{
    [Key(0)] public uint MessageCode { get; set; } = Codes.JoinOk;
    [Key(1)] public uint Vid { get; set; }
    [Key(2)] public float X { get; set; }
    [Key(3)] public float Y { get; set; }
    [Key(4)] public float Z { get; set; }
}

[MessagePackObject]
public class CMove : IPacketBase
{
    [Key(0)] public uint MessageCode { get; set; } = Codes.Move;
    [Key(1)] public float X { get; set; }
    [Key(2)] public float Y { get; set; }
    [Key(3)] public float Z { get; set; }
}

[MessagePackObject]
public class CAttack : IPacketBase
{
    [Key(0)] public uint MessageCode { get; set; } = Codes.Attack;
    [Key(1)] public uint TargetVid { get; set; }
}

/// <summary>AoE sweep attack — sphere around player.</summary>
[MessagePackObject]
public class CAoeAttack : IPacketBase
{
    [Key(0)] public uint MessageCode { get; set; } = Codes.AoeAttack;
    [Key(1)] public float Radius { get; set; } = 500f;
}
