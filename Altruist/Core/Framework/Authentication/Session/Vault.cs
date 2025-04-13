
namespace Altruist.Authentication;

public class AuthSessionData : IVaultModel
{
    public string Id { get; set; }
    public string Type { get; set; } = "AuthSessionData";
    public string PrincipalId { get; set; } = string.Empty;
    public string AccessToken { get; set; } = string.Empty;
    public string RefreshToken { get; set; } = string.Empty;
    public DateTime Expiration { get; set; }
    public string Ip { get; set; } = string.Empty;
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
    public TimeSpan CacheValidationInterval { get; set; } = TimeSpan.FromSeconds(10);

    public AuthSessionData() => Id = AccessToken;

    public bool IsValid() => Expiration > DateTime.UtcNow;
}