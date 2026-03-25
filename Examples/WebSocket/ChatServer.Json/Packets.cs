using System.Text.Json.Serialization;
using Altruist;
using MessagePack;

namespace ChatServer.Packets;

public static class ChatCodes
{
    public const uint ChatMessage = 1000;
    public const uint JoinRoom = 1001;
    public const uint LeaveRoom = 1002;
    public const uint UserList = 1003;
    public const uint SystemMessage = 1004;
}

[MessagePackObject]
public class CChatMessage : IPacketBase
{
    [Key(0)] public uint MessageCode { get; set; } = ChatCodes.ChatMessage;
    [JsonPropertyName("message")]
    [Key(1)] public string Message { get; set; } = "";
    [JsonPropertyName("room")]
    [Key(2)] public string Room { get; set; } = "";
}

[MessagePackObject]
public class CJoinRoom : IPacketBase
{
    [Key(0)] public uint MessageCode { get; set; } = ChatCodes.JoinRoom;
    [JsonPropertyName("username")]
    [Key(1)] public string Username { get; set; } = "";
    [JsonPropertyName("room")]
    [Key(2)] public string Room { get; set; } = "";
}

[MessagePackObject]
public class SChatMessage : IPacketBase
{
    [Key(0)] public uint MessageCode { get; set; } = ChatCodes.ChatMessage;
    [JsonPropertyName("sender")]
    [Key(1)] public string Sender { get; set; } = "";
    [JsonPropertyName("message")]
    [Key(2)] public string Message { get; set; } = "";
    [JsonPropertyName("timestamp")]
    [Key(3)] public long Timestamp { get; set; }
}

[MessagePackObject]
public class SSystemMessage : IPacketBase
{
    [Key(0)] public uint MessageCode { get; set; } = ChatCodes.SystemMessage;
    [JsonPropertyName("message")]
    [Key(1)] public string Message { get; set; } = "";
}

[MessagePackObject]
public class SUserList : IPacketBase
{
    [Key(0)] public uint MessageCode { get; set; } = ChatCodes.UserList;
    [JsonPropertyName("users")]
    [Key(1)] public string[] Users { get; set; } = [];
    [JsonPropertyName("room")]
    [Key(2)] public string Room { get; set; } = "";
}
