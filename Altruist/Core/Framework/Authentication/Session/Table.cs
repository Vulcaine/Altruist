using Altruist.UORM;

namespace Altruist.Authentication;

[Table("auth_session")]
public class SessionData : IVaultModel
{
    public string UserId { get; set; } = string.Empty;
    public string Token { get; set; } = string.Empty;
    public string Ip { get; set; } = string.Empty;
    public DateTime ExpiresAt { get; set; }
    public string Type { get; set; } = "AuthSessionData";
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
    public TimeSpan CacheValidationInterval { get; set; } = TimeSpan.FromSeconds(10);
}