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

[Vault("security")]
public class AuthTokenSessionModel : VaultModel, IIdGenerator
{
    [VaultColumn("principal-id")]
    public string PrincipalId { get; set; } = string.Empty;

    /// <summary>
    /// Optional fingerprint to bind the session to (device ID, browser hash, etc.)
    /// </summary>
    public string? Fingerprint { get; set; }

    [VaultColumn("access-token")]
    public string AccessToken { get; set; } = string.Empty;

    [VaultColumn("refresh-token")]
    public string RefreshToken { get; set; } = string.Empty;

    [VaultColumn("access-expiration")]
    public DateTime AccessExpiration { get; set; }

    [VaultColumn("refresh-expiration")]
    public DateTime RefreshExpiration { get; set; }
    public string Ip { get; set; } = string.Empty;
    public override DateTime Timestamp { get; set; } = DateTime.UtcNow;

    [VaultColumn("cache-invalidation-interval")]
    public TimeSpan CacheValidationInterval { get; set; } = TimeSpan.FromSeconds(10);

    public AuthTokenSessionModel()
    {
    }

    public bool IsAccessTokenValid() => AccessExpiration > DateTime.UtcNow;
    public bool IsRefreshTokenValid() => RefreshExpiration > DateTime.UtcNow;

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

public static class Sha256
{
    public static string Hash(string input)
    {
        using var sha = SHA256.Create();
        var bytes = Encoding.UTF8.GetBytes(input);
        var hashBytes = sha.ComputeHash(bytes);
        return Convert.ToHexString(hashBytes);
    }
}
