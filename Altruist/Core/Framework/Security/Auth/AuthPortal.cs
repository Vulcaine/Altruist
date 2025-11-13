namespace Altruist.Security;

public class AuthPortal : IPortal
{
    protected readonly IAuthService _authService;
    public AuthPortal(IAuthService authService)
    {
        _authService = authService;
    }

    [Gate("upgrade")]
    public Task UpgradeAuth(SessionAuthContext context, string clientId)
    {
        return _authService.Upgrade(context, clientId);
    }
}