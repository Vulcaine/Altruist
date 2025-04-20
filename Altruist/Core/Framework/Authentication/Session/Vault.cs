using System.Security.Cryptography;
using System.Text;

namespace Altruist.Security;

public class AuthTokenSessionModel : VaultModel, IIdGenerator
{
    public override string GenId { get; set; }
    public override string Type { get; set; }
    public string PrincipalId { get; set; } = string.Empty;

    /// <summary>
    /// Optional fingerprint to bind the session to (device ID, browser hash, etc.)
    /// </summary>
    public string? Fingerprint { get; set; }

    public string AccessToken { get; set; } = string.Empty;
    public string RefreshToken { get; set; } = string.Empty;
    public DateTime AccessExpiration { get; set; }
    public DateTime RefreshExpiration { get; set; }
    public string Ip { get; set; } = string.Empty;
    public override DateTime Timestamp { get; set; } = DateTime.UtcNow;
    public TimeSpan CacheValidationInterval { get; set; } = TimeSpan.FromSeconds(10);

    public AuthTokenSessionModel()
    {
        GenId = GenerateId();
        Type = GetType().Name;
    }

    public bool IsAccessTokenValid() => AccessExpiration > DateTime.UtcNow;
    public bool IsRefreshTokenValid() => RefreshExpiration > DateTime.UtcNow;

    public string GenerateId()
    {
        if (string.IsNullOrWhiteSpace(PrincipalId))
            throw new InvalidOperationException("PrincipalId must be set before generating GenId.");

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