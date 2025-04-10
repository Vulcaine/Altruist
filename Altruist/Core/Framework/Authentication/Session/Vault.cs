
namespace Altruist.Authentication;

public class AuthSessionData : IVaultModel
{
    public string UserId { get; set; } = string.Empty;
    public string Token { get; set; } = string.Empty;
    public string Ip { get; set; } = string.Empty;
    public DateTime ExpiresAt { get; set; }
    public string Type { get; set; } = "AuthSessionData";
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
    public TimeSpan CacheValidationInterval { get; set; } = TimeSpan.FromSeconds(10);

    public Task<List<IVaultModel>> PreLoad()
    {
        return Task.FromResult(new List<IVaultModel>());
    }
}