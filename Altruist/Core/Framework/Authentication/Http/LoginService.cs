namespace Altruist.Security;

public interface ILoginService
{
    public Task<AccountModel?> Login(LoginRequest request);
    public Task<AccountModel?> FindAccountByIdAsync(string id);
}

public abstract class LoginService<TAccount> : ILoginService where TAccount : AccountModel
{
    protected IVault<TAccount> _accountVault;

    public LoginService(IVault<TAccount> accountVault)
    {
        _accountVault = accountVault;
    }

    public abstract Task<AccountModel?> Login(LoginRequest request);
    public abstract Task<AccountModel?> FindAccountByIdAsync(string id);
}

public class UsernamePasswordLoginService<TAccount> : LoginService<TAccount> where TAccount : UsernamePasswordAccountVault
{
    public UsernamePasswordLoginService(IVault<TAccount> accountVault) : base(accountVault) { }

    public override async Task<AccountModel?> FindAccountByIdAsync(string id)
    {
        return await _accountVault.Where(acc => acc.GenId == id).FirstOrDefaultAsync();
    }

    public override async Task<AccountModel?> Login(LoginRequest request)
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