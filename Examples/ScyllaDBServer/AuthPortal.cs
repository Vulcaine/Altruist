using Altruist;
using Altruist.Security;
using Microsoft.Extensions.Logging;

[JwtShield]
public class MyAuthPortal : AuthPortal<SessionAuthContext>
{
    public MyAuthPortal(IPortalContext context, ILoggerFactory loggerFactory, SessionTokenIssuer issuer) : base(context, loggerFactory, issuer)
    {
    }
}