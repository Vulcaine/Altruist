namespace Altruist.Auth;

public interface ILoginService
{
    public Task<Account?> Login(LoginRequest request);
}

public abstract class LoginService<TAccount> : ILoginService where TAccount : Account
{
    protected IVault<TAccount> _accountVault;

    public LoginService(IVault<TAccount> accountVault)
    {
        _accountVault = accountVault;
    }

    public abstract Task<Account?> Login(LoginRequest request);
}

public class UsernamePasswordLoginService<TAccount> : LoginService<TAccount> where TAccount : UsernamePasswordAccount
{
    public UsernamePasswordLoginService(IVault<TAccount> accountVault) : base(accountVault) { }

    public override async Task<Account?> Login(LoginRequest request)
    {
        if (request is UsernamePasswordLoginRequest usernamePasswordLoginRequest)
        {
            if (_accountVault == null)
            {
                return null;
            }

            var account = await _accountVault
                .Where(a => a.Username == usernamePasswordLoginRequest.Username).FirstOrDefaultAsync();
            return account;
        }

        return null;
    }
}