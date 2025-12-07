using System.Security.Claims;

using Altruist;
using Altruist.Security;

public interface IAuthService
{
    /// <summary>
    /// Attempts to upgrade the session auth.
    /// Returns a newly issued token (IIssue) on success, or null on failure.
    /// </summary>
    Task<IIssue?> Upgrade(UpgradeAuthRequest context);

    /// <summary>
    /// Validates a raw JWT token and returns a ClaimsPrincipal if valid, otherwise null.
    /// </summary>
    Task<ClaimsPrincipal?> ValidateToken(string token);

    /// <summary>
    /// Validates the token carried in the SessionAuthContext and returns a ClaimsPrincipal if valid, otherwise null.
    /// </summary>
    Task<ClaimsPrincipal?> ValidateToken(UpgradeAuthRequest context);
}

[Service(typeof(IAuthService))]
[ConditionalOnConfig("altruist:security")]
public class AuthService : IAuthService
{
    protected readonly ISessionTokenIssuer _sessionTokenIssuer;
    private readonly TokenSessionSyncService? _syncService;
    private readonly ITokenValidator _tokenValidator;

    public AuthService(
        ISessionTokenIssuer issuer,
        TokenSessionSyncService? syncService,
        ITokenValidator tokenValidator)
    {
        _sessionTokenIssuer = issuer;
        _syncService = syncService;
        _tokenValidator = tokenValidator;
    }

    public virtual Task<IIssue?> Upgrade(UpgradeAuthRequest context)
    {
        return UpgradeAuth(context);
    }

    public async Task<ClaimsPrincipal?> ValidateToken(string token)
    {
        return await _tokenValidator.ValidateToken(token);
    }

    public async Task<ClaimsPrincipal?> ValidateToken(UpgradeAuthRequest context)
    {
        var raw = context.Token?.Split(';')[0] ?? string.Empty;
        if (string.IsNullOrWhiteSpace(raw))
            return null;

        return await _tokenValidator.ValidateToken(raw);
    }

    private async Task<IIssue?> UpgradeAuth(UpgradeAuthRequest context)
    {
        // Use the new ValidateToken(context) helper
        var claims = await ValidateToken(context);
        if (claims == null)
            return null;

        var groupKey = claims.FindFirst("GroupKey")?.Value;
        if (groupKey == null)
            return null;

        string? originalFingerprint = null;
        if (_syncService != null)
        {
            var old = await _syncService.DeleteAsync(context.Token, groupKey);
            if (old != null)
            {
                originalFingerprint = old.Fingerprint;
            }
        }

        var newToken = _sessionTokenIssuer.Issue();

        if (_syncService != null && newToken is TokenIssue tokenIssue)
        {
            var newAuthSession = new AuthTokenSessionModel
            {
                AccessToken = tokenIssue.AccessToken,
                AccessExpiration = tokenIssue.AccessExpiration,
                RefreshExpiration = tokenIssue.RefreshExpiration,
                RefreshToken = tokenIssue.RefreshToken,
                PrincipalId = claims.FindFirst(ClaimTypes.Name)?.Value!,
                Ip = claims.FindFirst("Ip")?.Value!,
                StorageId = tokenIssue.AccessToken,
                Fingerprint = originalFingerprint
            };

            await _syncService.SaveAsync(newAuthSession, groupKey);
        }
        else
        {
            return null;
        }

        return newToken;
    }
}
