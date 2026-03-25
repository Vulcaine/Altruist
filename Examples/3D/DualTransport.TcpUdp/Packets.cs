using Altruist;
using MessagePack;

namespace DualTransport;

// --- TCP packets (reliable) ---

[MessagePackObject]
public class CLogin : IPacketBase
{
    [Key(0)] public uint MessageCode { get; set; } = 100;
    [Key(1)] public string Name { get; set; } = "";
}

[MessagePackObject]
public class SLoginOk : IPacketBase
{
    [Key(0)] public uint MessageCode { get; set; } = 101;
    [Key(1)] public string PlayerId { get; set; } = "";
}

[MessagePackObject]
public class CChat : IPacketBase
{
    [Key(0)] public uint MessageCode { get; set; } = 200;
    [Key(1)] public string Message { get; set; } = "";
}

[MessagePackObject]
public class SChatBroadcast : IPacketBase
{
    [Key(0)] public uint MessageCode { get; set; } = 201;
    [Key(1)] public string Sender { get; set; } = "";
    [Key(2)] public string Message { get; set; } = "";
}

// --- UDP packets (fast, unreliable) ---

[MessagePackObject]
public class CPositionUpdate : IPacketBase
{
    [Key(0)] public uint MessageCode { get; set; } = 300;
    [Key(1)] public float X { get; set; }
    [Key(2)] public float Y { get; set; }
    [Key(3)] public float Z { get; set; }
    [Key(4)] public float Yaw { get; set; }
}

[MessagePackObject]
public class SPositionBroadcast : IPacketBase
{
    [Key(0)] public uint MessageCode { get; set; } = 301;
    [Key(1)] public string PlayerId { get; set; } = "";
    [Key(2)] public float X { get; set; }
    [Key(3)] public float Y { get; set; }
    [Key(4)] public float Z { get; set; }
    [Key(5)] public float Yaw { get; set; }
}
