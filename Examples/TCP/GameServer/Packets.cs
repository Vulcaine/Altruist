using Altruist;
using MessagePack;

namespace GameServer.Packets;

public static class GameCodes
{
    public const uint Login = 1000;
    public const uint LoginSuccess = 1001;
    public const uint LoginFailure = 1002;
    public const uint Move = 1010;
    public const uint EntitySpawn = 1020;
    public const uint EntityDespawn = 1021;
    public const uint EntityUpdate = 1022;
    public const uint Chat = 1030;
    public const uint ChatBroadcast = 1031;
}

// Client -> Server

[MessagePackObject]
public class CLogin : IPacketBase
{
    [Key(0)] public uint MessageCode { get; set; } = GameCodes.Login;
    [Key(1)] public string Username { get; set; } = "";
}

[MessagePackObject]
public class CMove : IPacketBase
{
    [Key(0)] public uint MessageCode { get; set; } = GameCodes.Move;
    [Key(1)] public float X { get; set; }
    [Key(2)] public float Y { get; set; }
    [Key(3)] public float Speed { get; set; }
}

[MessagePackObject]
public class CChat : IPacketBase
{
    [Key(0)] public uint MessageCode { get; set; } = GameCodes.Chat;
    [Key(1)] public string Message { get; set; } = "";
}

// Server -> Client

[MessagePackObject]
public class SLoginSuccess : IPacketBase
{
    [Key(0)] public uint MessageCode { get; set; } = GameCodes.LoginSuccess;
    [Key(1)] public string PlayerId { get; set; } = "";
    [Key(2)] public float SpawnX { get; set; }
    [Key(3)] public float SpawnY { get; set; }
}

[MessagePackObject]
public class SLoginFailure : IPacketBase
{
    [Key(0)] public uint MessageCode { get; set; } = GameCodes.LoginFailure;
    [Key(1)] public string Reason { get; set; } = "";
}

[MessagePackObject]
public class SEntitySpawn : IPacketBase
{
    [Key(0)] public uint MessageCode { get; set; } = GameCodes.EntitySpawn;
    [Key(1)] public string EntityId { get; set; } = "";
    [Key(2)] public string Name { get; set; } = "";
    [Key(3)] public float X { get; set; }
    [Key(4)] public float Y { get; set; }
}

[MessagePackObject]
public class SEntityDespawn : IPacketBase
{
    [Key(0)] public uint MessageCode { get; set; } = GameCodes.EntityDespawn;
    [Key(1)] public string EntityId { get; set; } = "";
}

[MessagePackObject]
public class SEntityUpdate : IPacketBase
{
    [Key(0)] public uint MessageCode { get; set; } = GameCodes.EntityUpdate;
    [Key(1)] public string EntityId { get; set; } = "";
    [Key(2)] public float X { get; set; }
    [Key(3)] public float Y { get; set; }
}

[MessagePackObject]
public class SChatBroadcast : IPacketBase
{
    [Key(0)] public uint MessageCode { get; set; } = GameCodes.ChatBroadcast;
    [Key(1)] public string Sender { get; set; } = "";
    [Key(2)] public string Message { get; set; } = "";
}
