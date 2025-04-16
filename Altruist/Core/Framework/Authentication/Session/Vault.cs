
namespace Altruist.Security;


public class AuthTokenSessionModel : VaultModel
{
    public override string GenId { get; set; }
    public override string Type { get; set; }
    public string PrincipalId { get; set; } = string.Empty;
    public string AccessToken { get; set; } = string.Empty;
    public string RefreshToken { get; set; } = string.Empty;
    public DateTime AccessExpiration { get; set; }
    public DateTime RefreshExpiration { get; set; }
    public string Ip { get; set; } = string.Empty;
    public override DateTime Timestamp { get; set; } = DateTime.UtcNow;
    public TimeSpan CacheValidationInterval { get; set; } = TimeSpan.FromSeconds(10);

    public AuthTokenSessionModel()
    {
        GenId = PrincipalId;
        Type = GetType().Name;
    }

    public bool IsAccessTokenValid() => AccessExpiration > DateTime.UtcNow;
    public bool IsRefreshTokenValid() => RefreshExpiration > DateTime.UtcNow;
}