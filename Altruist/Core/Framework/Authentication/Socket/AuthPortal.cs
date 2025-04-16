using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Altruist.Security;

public abstract class AuthPortal<TAuthContext> : Portal where TAuthContext : ISessionAuthContext
{
    protected IIssuer Issuer;
    private readonly TokenSessionSyncService? _syncService;

    protected AuthPortal(IPortalContext context, ILoggerFactory loggerFactory, IIssuer issuer, IServiceProvider serviceProvider) : base(context, loggerFactory)
    {
        Issuer = issuer;
        _syncService = serviceProvider.GetService<TokenSessionSyncService>();
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