/* 
Copyright 2025 Aron Gere

Licensed under the Apache License, Version 2.0 (the "License");
you may not use this file except in compliance with the License.
You may obtain a copy of the License at

    http://www.apache.org/licenses/LICENSE-2.0

Unless required by applicable law or agreed to in writing, software
distributed under the License is distributed on an "AS IS" BASIS,
WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
See the License for the specific language governing permissions and
limitations under the License.
*/

using System.Text.Json.Serialization;
using MessagePack;

namespace Altruist.Security;

public interface IIssue : IPacketBase
{

}

[MessagePackObject]
public abstract class TokenIssue : IIssue
{
    [Key(0)]
    [JsonPropertyName("header")]
    public PacketHeader Header { get; set; }

    [Key(1)]
    [JsonPropertyName("type")]
    public virtual string Type { get; set; } = "TokenIssue";

    [Key(2)]
    [JsonPropertyName("accessToken")]
    public string AccessToken { get; set; } = "";

    [Key(3)]
    [JsonPropertyName("refreshToken")]
    public string RefreshToken { get; set; } = "";

    [Key(4)]
    [JsonPropertyName("accessExpiration")]
    public DateTime AccessExpiration { get; set; } = DateTime.UtcNow + TimeSpan.FromMinutes(30);

    [Key(5)]
    [JsonPropertyName("refreshExpiration")]
    public DateTime RefreshExpiration { get; set; } = DateTime.UtcNow + TimeSpan.FromDays(7);

    [IgnoreMember]
    [JsonIgnore]
    public string Algorithm { get; set; } = "";
}


public interface ISessionAuthContext : IPacketBase
{
    public string StatelessToken { get; }
}

[MessagePackObject]
public struct SessionAuthContext : ISessionAuthContext
{
    [JsonPropertyName("header")]
    [Key(0)]
    public PacketHeader Header { get; set; }

    [JsonPropertyName("type")]
    [Key(1)]
    public string Type { get; set; }

    [JsonPropertyName("token")]
    [Key(2)]
    public string StatelessToken { get; set; } = string.Empty;

    public SessionAuthContext()
    {
        Header = new PacketHeader();
        Type = nameof(SessionAuthContext);
    }

    public SessionAuthContext(string sender, string? receiver = null)
    {
        Header = new PacketHeader(sender, receiver);
        Type = nameof(SessionAuthContext);
    }
}