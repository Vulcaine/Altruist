using Altruist;
using Altruist.Security;
using Microsoft.Extensions.Logging;

[JwtShield]
public class MyAuthPortal : AuthPortal<SessionAuthContext>
{
    public MyAuthPortal(IPortalContext context, ILoggerFactory loggerFactory, SessionTokenIssuer issuer, IServiceProvider serviceProvider) : base(context, loggerFactory, issuer, serviceProvider)
    {
    }
}