
namespace Altruist.Authentication;

public class AuthSessionVault : VaultModel
{
    public override string GenId { get; set; }
    public override string Type { get; set; } = "AuthSessionData";
    public string PrincipalId { get; set; } = string.Empty;
    public string AccessToken { get; set; } = string.Empty;
    public string RefreshToken { get; set; } = string.Empty;
    public DateTime Expiration { get; set; }
    public string Ip { get; set; } = string.Empty;
    public override DateTime Timestamp { get; set; } = DateTime.UtcNow;
    public TimeSpan CacheValidationInterval { get; set; } = TimeSpan.FromSeconds(10);

    public AuthSessionVault() => GenId = PrincipalId;

    public bool IsValid() => Expiration > DateTime.UtcNow;
}