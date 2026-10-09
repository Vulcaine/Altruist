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

using System.Security.Cryptography;
using System.Text;

using Altruist.UORM;

namespace Altruist.Security;

/// <summary>
/// A stored server-side session of the legacy token flows (<see cref="AuthController"/>, <see cref="IAuthService"/>,
/// <c>mode: session</c>): the issued token pair, expirations, client IP and optional fingerprint. Kept in the cache by
/// <see cref="TokenSessionSyncService"/> (keyed by <see cref="IVaultModel.StorageId"/>, which the controllers set to the
/// access token) and also in the <c>security</c> vault when one is available. Registered when <c>altruist:security</c> exists.
/// </summary>
[Vault("security")]
[ConditionalOnConfig("altruist:security")]
public class AuthTokenSessionModel : VaultModel, IIdGenerator
{
    /// <summary>The principal (account id) the session belongs to.</summary>
    [VaultColumn("principal-id")]
    public string PrincipalId { get; set; } = string.Empty;

    /// <summary>
    /// Optional fingerprint to bind the session to (device ID, browser hash, etc.)
    /// </summary>
    public string? Fingerprint { get; set; }

    /// <summary>The access token, with protocol suffix.</summary>
    [VaultColumn("access-token")]
    public string AccessToken { get; set; } = string.Empty;

    /// <summary>The refresh token, with protocol suffix.</summary>
    [VaultColumn("refresh-token")]
    public string RefreshToken { get; set; } = string.Empty;

    /// <summary>UTC expiry of the access token, as issued; <see cref="SessionTokenAuth"/> rejects the session from then on.</summary>
    [VaultColumn("access-expiration")]
    public DateTime AccessExpiration { get; set; }

    /// <summary>UTC expiry of the refresh token.</summary>
    [VaultColumn("refresh-expiration")]
    public DateTime RefreshExpiration { get; set; }
    /// <summary>The client IP the session is bound to (checked by <see cref="SessionTokenAuth"/>).</summary>
    public string Ip { get; set; } = string.Empty;
    /// <inheritdoc/>
    public override DateTime Timestamp { get; set; } = DateTime.UtcNow;

    /// <summary>How long <see cref="SessionTokenAuth"/> trusts its in-process copy before re-reading the session (default 10 seconds).</summary>
    [VaultColumn("cache-invalidation-interval")]
    public TimeSpan CacheValidationInterval { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>Creates an empty session.</summary>
    public AuthTokenSessionModel()
    {
    }

    /// <summary>True while <see cref="AccessExpiration"/> is in the future (UTC).</summary>
    public bool IsAccessTokenValid() => AccessExpiration > DateTime.UtcNow;
    /// <summary>True while <see cref="RefreshExpiration"/> is in the future (UTC).</summary>
    public bool IsRefreshTokenValid() => RefreshExpiration > DateTime.UtcNow;

    /// <summary>An id derived from the principal (and fingerprint when set): uppercase hex SHA-256 of <c>principal</c> or <c>principal:fingerprint</c>.</summary>
    /// <exception cref="InvalidOperationException">When <see cref="PrincipalId"/> is empty.</exception>
    public string GenerateId()
    {
        if (string.IsNullOrWhiteSpace(PrincipalId))
            throw new InvalidOperationException("PrincipalId must be set before generating StorageId.");

        var combined = string.IsNullOrWhiteSpace(Fingerprint)
            ? PrincipalId
            : $"{PrincipalId}:{Fingerprint}";

        return Sha256.Hash(combined);
    }
}

/// <summary>SHA-256 helper. For token hashes use <see cref="OpaqueToken.Hash"/> (lowercase hex) instead; this one returns uppercase hex.</summary>
public static class Sha256
{
    /// <summary>Uppercase hex SHA-256 of the UTF-8 bytes of <paramref name="input"/> (64 characters).</summary>
    public static string Hash(string input)
    {
        using var sha = SHA256.Create();
        var bytes = Encoding.UTF8.GetBytes(input);
        var hashBytes = sha.ComputeHash(bytes);
        return Convert.ToHexString(hashBytes);
    }
}
