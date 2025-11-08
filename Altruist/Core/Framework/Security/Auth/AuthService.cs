using System.Security.Claims;
using Altruist;
using Altruist.Security;

public interface IAuthService<TAuthContext>
{

}

[Service(typeof(IAuthService<>))]
public class AuthService<TAuthContext> : IAuthService<TAuthContext> where TAuthContext : ISessionAuthContext
{
    protected IIssuer _issuer;
    private readonly IConnectionManager _connectionManager;
    private readonly TokenSessionSyncService? _syncService;
    private readonly ITokenValidator _tokenValidator;
    private readonly IAltruistRouter _router;


    public AuthService(
        IIssuer issuer,
        IConnectionManager connectionManager, TokenSessionSyncService? syncService, JwtTokenValidator tokenValidator, IAltruistRouter router)
    {
        _issuer = issuer;
        _connectionManager = connectionManager;
        _syncService = syncService;
        _tokenValidator = tokenValidator;
        _router = router;
    }

    public virtual async Task Upgrade(TAuthContext context, string clientId)
    {
        var connection = await _connectionManager.GetConnectionAsync(clientId);
        if (connection != null)
        {
            var token = await UpgradeAuth(context, clientId);

            if (token != null)
            {
                // authorized close
                await _router.Client.SendAsync(clientId, token);
            }

            // unauthorized close
            await connection.CloseOutputAsync();
            await connection.CloseAsync();
        }
    }

    public virtual async Task<IIssue?> UpgradeAuth(TAuthContext context, string clientId)
    {
        var token = context.StatelessToken.Split(";")[0];
        var claims = _tokenValidator.ValidateToken(token);
        if (claims == null)
        {
            return null;
        }

        var groupKey = claims.FindFirst("GroupKey")?.Value;
        if (groupKey == null)
        {
            return null;
        }

        string? originalFingerprint = null;
        if (_syncService != null)
        {
            var old = await _syncService.DeleteAsync(context.StatelessToken, groupKey);

            if (old == null)
            {
                return null;
            }

            originalFingerprint = old.Fingerprint;
        }

        var newToken = _issuer.Issue();

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
                SysId = tokenIssue.AccessToken,
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