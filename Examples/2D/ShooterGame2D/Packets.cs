using Altruist;
using MessagePack;

namespace ShooterGame2D;

public static class Codes
{
    public const uint Join = 100;
    public const uint JoinOk = 101;
    public const uint Move = 110;
    public const uint Shoot = 120;
    public const uint Hit = 121;
    public const uint Death = 130;
    public const uint Spawn = 140;
    public const uint Despawn = 141;
}

// Client -> Server

[MessagePackObject]
public class CJoin : IPacketBase
{
    [Key(0)] public uint MessageCode { get; set; } = Codes.Join;
    [Key(1)] public string Name { get; set; } = "";
}

[MessagePackObject]
public class CMove : IPacketBase
{
    [Key(0)] public uint MessageCode { get; set; } = Codes.Move;
    [Key(1)] public int X { get; set; }
    [Key(2)] public int Y { get; set; }
}

[MessagePackObject]
public class CShoot : IPacketBase
{
    [Key(0)] public uint MessageCode { get; set; } = Codes.Shoot;
    [Key(1)] public uint TargetVid { get; set; }
}

// Server -> Client

[MessagePackObject]
public class SJoinOk : IPacketBase
{
    [Key(0)] public uint MessageCode { get; set; } = Codes.JoinOk;
    [Key(1)] public uint Vid { get; set; }
    [Key(2)] public int X { get; set; }
    [Key(3)] public int Y { get; set; }
}

[MessagePackObject]
public class SHit : IPacketBase
{
    [Key(0)] public uint MessageCode { get; set; } = Codes.Hit;
    [Key(1)] public uint TargetVid { get; set; }
    [Key(2)] public int Damage { get; set; }
    [Key(3)] public int RemainingHp { get; set; }
}

[MessagePackObject]
public class SDeath : IPacketBase
{
    [Key(0)] public uint MessageCode { get; set; } = Codes.Death;
    [Key(1)] public uint Vid { get; set; }
}
