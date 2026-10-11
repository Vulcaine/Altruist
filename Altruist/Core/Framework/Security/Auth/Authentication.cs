
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
namespace Altruist.Security;

/// <summary>
/// The identity of an authenticated connection or request, produced by an <see cref="IShieldAuth"/> handler
/// (<see cref="JwtAuth"/>, <see cref="SessionTokenAuth"/>, <see cref="TicketShieldAuth"/>) and returned by
/// <see cref="ShieldAttribute.AuthenticateNonHttpAsync"/>. Portals use it to know who a socket belongs to
/// and how long it may stay authenticated.
/// </summary>
/// <remarks>
/// The literal value <c>"Unknown"</c> in <see cref="PrincipalId"/>, <see cref="Ip"/> or <see cref="GroupKey"/>
/// marks a field the handler could not determine; <see cref="IsAlive"/> treats such details as dead.
/// </remarks>
public class AuthDetails
{
    /// <summary>The raw credential the identity was established with (empty for ticket-authenticated connections).</summary>
    public string Token { get; set; }
    /// <summary>The authenticated principal (account / user id).</summary>
    public string PrincipalId { get; set; }
    /// <summary>The client address the identity is bound to, as text.</summary>
    public string Ip { get; set; }
    /// <summary>Key grouping all sessions of a principal (usually the principal id); used to invalidate sessions together.</summary>
    public string GroupKey { get; set; }
    /// <summary>UTC instant after which the identity is no longer valid.</summary>
    public DateTimeOffset Expiry { get; set; }

    /// <summary>Creates details that expire <paramref name="validityPeriod"/> from now (UTC).</summary>
    /// <param name="token">The raw credential.</param>
    /// <param name="PrincipalId">The authenticated principal id.</param>
    /// <param name="Ip">The client address.</param>
    /// <param name="GroupKey">The session group key.</param>
    /// <param name="validityPeriod">How long from now the identity stays valid.</param>
    public AuthDetails(string token, string PrincipalId, string Ip, string GroupKey, TimeSpan validityPeriod)
    {
        Token = token;
        Expiry = DateTimeOffset.UtcNow.Add(validityPeriod);
        this.PrincipalId = PrincipalId;
        this.Ip = Ip;
        this.GroupKey = GroupKey;
    }

    /// <summary>
    /// True while <see cref="Expiry"/> lies in the future and none of <see cref="Ip"/>, <see cref="PrincipalId"/>,
    /// <see cref="GroupKey"/> is the placeholder <c>"Unknown"</c>.
    /// </summary>
    public bool IsAlive() => DateTimeOffset.UtcNow < Expiry && !(Ip == "Unknown" || PrincipalId == "Unknown" || GroupKey == "Unknown");

    /// <summary>Seconds until <see cref="Expiry"/> (negative once expired).</summary>
    public double TimeLeftSeconds() => (Expiry - DateTimeOffset.UtcNow).TotalSeconds;
}

