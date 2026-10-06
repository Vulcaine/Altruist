/*
Copyright 2025 Aron Gere
Licensed under the Apache License, Version 2.0
*/

using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;

using Microsoft.AspNetCore.Http;

namespace Altruist.Security;

public static class SecurityExtensions
{
    /// <summary>
    /// The principal id of an authenticated user: the <c>sub</c> claim, or
    /// <see cref="ClaimTypes.NameIdentifier"/> where inbound claims are mapped
    /// (<c>altruist:security:jwt:map-inbound-claims</c>). Null when unauthenticated.
    /// </summary>
    public static string? PrincipalId(this ClaimsPrincipal user) =>
        user.FindFirstValue(JwtRegisteredClaimNames.Sub) ?? user.FindFirstValue(ClaimTypes.NameIdentifier);

    /// <summary>
    /// The display name of an authenticated user: the identity's name claim
    /// (<c>altruist:security:jwt:name-claim</c>), else the <c>name</c> or <see cref="ClaimTypes.Name"/> claim.
    /// </summary>
    public static string? PrincipalName(this ClaimsPrincipal user) =>
        user.Identity?.Name ?? user.FindFirstValue(JwtRegisteredClaimNames.Name) ?? user.FindFirstValue(ClaimTypes.Name);

    /// <summary>
    /// The client's address: <see cref="ConnectionInfo.RemoteIpAddress"/>, which is the real client
    /// once forwarded headers (behind a trusted proxy) and the fleet relay have restored it.
    /// "unknown" when there is none (e.g. in-process test hosts).
    /// </summary>
    public static string ClientIp(this HttpContext context) =>
        context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
}
