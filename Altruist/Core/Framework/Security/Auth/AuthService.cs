using System.Security.Claims;

using Altruist;
using Altruist.Security;

/// <summary>
/// Token validation and the "upgrade" flow: exchanges a valid token for a freshly issued session token
/// (<see cref="ISessionTokenIssuer"/>), carrying the session over in <see cref="TokenSessionSyncService"/>.
/// Used by <see cref="Altruist.Security.Http.JwtAuthController"/>'s <c>upgrade</c> endpoint. Registered as a
/// singleton by <see cref="AuthService"/> when an <c>altruist:security</c> section exists.
/// </summary>
/// <remarks>
/// For new code, prefer the stateless pair <see cref="IAccessTokenIssuer"/> + <see cref="IRefreshTokenService"/>
/// for HTTP sign-in and <see cref="IConnectionTicketService"/> for WebSocket connections.
/// </remarks>
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
/// <summary>
/// Default <see cref="IAuthService"/> (registered when <c>altruist:security</c> exists). Validates with the
/// registered <see cref="ITokenValidator"/> (JWT in <c>mode: jwt</c>). Override <see cref="Upgrade"/> to customise.
/// </summary>
public class AuthService : IAuthService
{
    /// <summary>The issuer of the session token handed out by <see cref="Upgrade"/>.</summary>
    protected readonly ISessionTokenIssuer _sessionTokenIssuer;
    private readonly TokenSessionSyncService? _syncService;
    private readonly ITokenValidator _tokenValidator;

    /// <summary>Creates the service (resolved by DI).</summary>
    /// <param name="issuer">Issues the upgraded session token.</param>
    /// <param name="syncService">Session store; when null, <see cref="Upgrade"/> always fails.</param>
    /// <param name="tokenValidator">Validates incoming tokens.</param>
    public AuthService(
        ISessionTokenIssuer issuer,
        TokenSessionSyncService? syncService,
        ITokenValidator tokenValidator)
    {
        _sessionTokenIssuer = issuer;
        _syncService = syncService;
        _tokenValidator = tokenValidator;
    }

    /// <summary>
    /// Validates <see cref="UpgradeAuthRequest.Token"/> (it must carry a <c>GroupKey</c> claim), deletes the old
    /// session from <see cref="TokenSessionSyncService"/> (keeping its fingerprint), issues a new
    /// <see cref="SessionToken"/> and saves it under the group key. Returns null when the token is invalid, has no
    /// group key, or no session store is registered.
    /// </summary>
    public virtual Task<IIssue?> Upgrade(UpgradeAuthRequest context)
    {
        return UpgradeAuth(context);
    }

    /// <inheritdoc/>
    public async Task<ClaimsPrincipal?> ValidateToken(string token)
    {
        return await _tokenValidator.ValidateToken(token);
    }

    /// <summary>Validates the part of <see cref="UpgradeAuthRequest.Token"/> before the first <c>;</c> (the protocol suffix is dropped); null when empty or invalid.</summary>
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
