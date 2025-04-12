namespace Altruist.Auth;

public interface ILoginService<T> where T : LoginRequest
{
    public Task<bool> Login(LoginRequest request);
}

public abstract class LoginService<T> : ILoginService<T> where T : LoginRequest
{
    protected IVault<Account> _accountVault;

    public LoginService(IVault<Account> accountVault)
    {
        _accountVault = accountVault;
    }

    public abstract Task<bool> Login(LoginRequest request);
}

public class UsernamePasswordLoginService : LoginService<UsernamePasswordLoginRequest>
{
    public UsernamePasswordLoginService(IVault<Account> accountVault) : base(accountVault) { }

    public override async Task<bool> Login(LoginRequest request)
    {
        if (request is UsernamePasswordLoginRequest usernamePasswordLoginRequest)
        {
            var vault = _accountVault as IVault<UsernamePasswordAccount>;

            if (vault == null)
            {
                return false;
            }

            var account = await vault
                .Where(a => a.Username == usernamePasswordLoginRequest.Username).FirstOrDefaultAsync();
            return account != null;
        }

        return false;
    }
}