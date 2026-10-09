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

/// <summary>Marker for whatever an <see cref="IIssuer"/> hands out (usually a <see cref="TokenIssue"/>).</summary>
public interface IIssue
{

}

/// <summary>
/// An access/refresh token pair as returned to clients by the legacy issuers (<see cref="SessionTokenIssuer"/>,
/// <see cref="JwtTokenIssuer"/>). Serializable as JSON and MessagePack. Tokens carry a protocol suffix
/// (<c>;session</c> or <c>;jwt</c>) that validators strip.
/// </summary>
public abstract class TokenIssue : IIssue
{
    /// <summary>Token kind discriminator (<see cref="IssuerKeys"/> values).</summary>
    [Key(0)]
    [JsonPropertyName("type")]
    public virtual string Type { get; set; } = "TokenIssue";

    /// <summary>The access token presented on each request, with its protocol suffix.</summary>
    [Key(1)]
    [JsonPropertyName("accessToken")]
    public string AccessToken { get; set; } = "";

    /// <summary>The refresh token exchanged for a new pair, with its protocol suffix.</summary>
    [Key(2)]
    [JsonPropertyName("refreshToken")]
    public string RefreshToken { get; set; } = "";

    // used to identify accounts/users
    /// <summary>The principal (account/user) the tokens identify; empty when the issuer had no subject claim.</summary>
    [Key(3)]
    [JsonPropertyName("principalId")]
    public string PrincipalId { get; set; } = "";

    /// <summary>UTC expiry of <see cref="AccessToken"/>. Defaults to 30 minutes from construction; issuers overwrite it (note that <see cref="JwtTokenIssuer"/> does not, while its JWT itself expires after one hour).</summary>
    [Key(4)]
    [JsonPropertyName("accessExpiration")]
    public DateTime AccessExpiration { get; set; } = DateTime.UtcNow + TimeSpan.FromMinutes(30);

    /// <summary>UTC expiry of <see cref="RefreshToken"/>. Defaults to 7 days from construction; issuers may overwrite it.</summary>
    [Key(5)]
    [JsonPropertyName("refreshExpiration")]
    public DateTime RefreshExpiration { get; set; } = DateTime.UtcNow + TimeSpan.FromDays(7);

    /// <summary>Signing algorithm of JWT tokens (e.g. HS256); not serialized.</summary>
    [IgnoreMember]
    [JsonIgnore]
    public string Algorithm { get; set; } = "";
}


/// <summary>A packet carrying a token to authenticate a socket session with.</summary>
public interface ISessionAuthContext : IPacketBase
{
    /// <summary>The raw token.</summary>
    public string Token { get; }
}

/// <summary>The session-auth packet (<c>PacketCodes.SessionAuth</c>) a client sends with its token over a socket transport.</summary>
[MessagePackObject]
public struct SessionAuthContext : ISessionAuthContext
{
    /// <summary>Packet code; <c>PacketCodes.SessionAuth</c>.</summary>
    [JsonPropertyName("messageCode")]
    [Key(0)]
    public uint MessageCode { get; set; }

    /// <summary>Packet routing header.</summary>
    [JsonPropertyName("header")]
    [Key(1)]
    public PacketHeader Header { get; set; }

    /// <summary>Packet type name (<c>SessionAuthContext</c>).</summary>
    [JsonPropertyName("type")]
    [Key(2)]
    public string Type { get; set; }

    /// <summary>The token to authenticate with.</summary>
    [JsonPropertyName("token")]
    [Key(3)]
    public string Token { get; set; } = string.Empty;

    /// <summary>Creates the packet with its message code and type set.</summary>
    public SessionAuthContext()
    {
        MessageCode = PacketCodes.SessionAuth;
        Header = default;
        Type = nameof(SessionAuthContext);
    }
}

/// <summary>Body of the <c>upgrade</c> endpoint / argument of <see cref="IAuthService.Upgrade"/>: a token to trade for a new session token.</summary>
public struct UpgradeAuthRequest
{
    /// <summary>The current token (an optional <c>;protocol</c> suffix is ignored when validating).</summary>
    [JsonPropertyName("token")]
    [Key(2)]
    public string Token { get; set; } = string.Empty;

    /// <summary>Optional client id; the controller falls back to the connection id.</summary>
    [JsonPropertyName("clientId")]
    [Key(3)]
    public string? ClientId { get; set; }

    /// <summary>Creates an empty request.</summary>
    public UpgradeAuthRequest()
    {

    }
}
