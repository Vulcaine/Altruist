using Microsoft.Extensions.Logging;

namespace Altruist.Security;

public abstract class AuthPortal<TAuthContext> : Portal where TAuthContext : ISessionAuthContext
{
    protected IIssuer Issuer;

    protected AuthPortal(IPortalContext context, ILoggerFactory loggerFactory, IIssuer issuer) : base(context, loggerFactory)
    {
        Issuer = issuer;
    }

    [Gate("upgrade")]
    public virtual async Task Upgrade(TAuthContext context, string clientId)
    {
        var connection = await GetConnectionAsync(clientId);
        if (connection != null)
        {
            var token = await UpgradeAuth(context, clientId);
            await Router.Client.SendAsync(clientId, token);
            await connection.CloseOutputAsync();
            await connection.CloseAsync();
        }
    }

    public virtual Task<IIssue> UpgradeAuth(TAuthContext context, string clientId)
    {
        return Task.FromResult(Issuer.Issue());
    }
}