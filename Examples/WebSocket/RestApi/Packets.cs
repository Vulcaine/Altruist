using System.Text.Json.Serialization;
using Altruist;
using MessagePack;

namespace RestApi.Packets;

public static class NotifyCodes
{
    public const uint TaskCreated = 1000;
    public const uint TaskCompleted = 1001;
    public const uint TaskDeleted = 1002;
}

[MessagePackObject]
public class STaskNotification : IPacketBase
{
    [Key(0)] public uint MessageCode { get; set; }
    [JsonPropertyName("taskId")]
    [Key(1)] public string TaskId { get; set; } = "";
    [JsonPropertyName("title")]
    [Key(2)] public string Title { get; set; } = "";
}
