using Altruist;
using MessagePack;

namespace ChatServer.MessagePack.Packets;

public static class ChatCodes
{
    public const uint ChatMessage = 1000;
    public const uint JoinRoom = 1001;
    public const uint SystemMessage = 1002;
    public const uint UserJoined = 1003;
    public const uint UserLeft = 1004;
}

[MessagePackObject]
public class CChatMessage : IPacketBase
{
    [Key(0)] public uint MessageCode { get; set; } = ChatCodes.ChatMessage;
    [Key(1)] public string Message { get; set; } = "";
}

[MessagePackObject]
public class CJoinRoom : IPacketBase
{
    [Key(0)] public uint MessageCode { get; set; } = ChatCodes.JoinRoom;
    [Key(1)] public string Username { get; set; } = "";
    [Key(2)] public string Room { get; set; } = "general";
}

[MessagePackObject]
public class SChatMessage : IPacketBase
{
    [Key(0)] public uint MessageCode { get; set; } = ChatCodes.ChatMessage;
    [Key(1)] public string Sender { get; set; } = "";
    [Key(2)] public string Message { get; set; } = "";
    [Key(3)] public long Timestamp { get; set; }
}

[MessagePackObject]
public class SSystemMessage : IPacketBase
{
    [Key(0)] public uint MessageCode { get; set; } = ChatCodes.SystemMessage;
    [Key(1)] public string Message { get; set; } = "";
}

[MessagePackObject]
public class SUserJoined : IPacketBase
{
    [Key(0)] public uint MessageCode { get; set; } = ChatCodes.UserJoined;
    [Key(1)] public string Username { get; set; } = "";
    [Key(2)] public string Room { get; set; } = "";
}

[MessagePackObject]
public class SUserLeft : IPacketBase
{
    [Key(0)] public uint MessageCode { get; set; } = ChatCodes.UserLeft;
    [Key(1)] public string Username { get; set; } = "";
    [Key(2)] public string Room { get; set; } = "";
}
