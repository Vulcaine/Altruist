using System.IdentityModel.Tokens.Jwt;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Altruist.Security;

public abstract class AuthPortal<TAuthContext> : Portal where TAuthContext : ISessionAuthContext
{
    protected IIssuer Issuer;
    private readonly TokenSessionSyncService? _syncService;
    private readonly JwtTokenValidator _tokenValidator;

    protected AuthPortal(IPortalContext context, ILoggerFactory loggerFactory, IIssuer issuer, IServiceProvider serviceProvider) : base(context, loggerFactory)
    {
        Issuer = issuer;
        _syncService = serviceProvider.GetService<TokenSessionSyncService>();
        _tokenValidator = serviceProvider.GetRequiredService<JwtTokenValidator>();
    }

    [Gate("upgrade")]
    public virtual async Task Upgrade(TAuthContext context, string clientId)
    {
        var connection = await GetConnectionAsync(clientId);
        if (connection != null)
        {
            var token = await UpgradeAuth(context, clientId);

            if (token != null)
            {
                // authorized close
                await Router.Client.SendAsync(clientId, token);
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

        if (_syncService != null)
        {
            await _syncService.DeleteAsync(context.StatelessToken, groupKey);
        }

        return Issuer.Issue();
    }
}