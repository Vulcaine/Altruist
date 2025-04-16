namespace Altruist.Security;

public interface ILoginService
{
    public Task<AccountModel?> Signup(SignupRequest request);
    public Task<AccountModel?> Login(LoginRequest request);
    public Task<AccountModel?> FindAccountByIdAsync(string id);
}

public abstract class LoginService<TAccount> : ILoginService where TAccount : AccountModel
{
    protected IVault<TAccount> _accountVault;
    protected IPasswordHasher _passwordHasher;

    public LoginService(IVault<TAccount> accountVault, IPasswordHasher passwordHasher)
    {
        _accountVault = accountVault;
        _passwordHasher = passwordHasher;
    }

    public abstract Task<AccountModel?> Login(LoginRequest request);
    public abstract Task<AccountModel?> FindAccountByIdAsync(string id);
    public abstract Task<AccountModel?> Signup(SignupRequest request);
}

public class UsernamePasswordLoginService<TAccount> : LoginService<TAccount> where TAccount : UsernamePasswordAccountVault
{
    public UsernamePasswordLoginService(IVault<TAccount> accountVault, IPasswordHasher passwordHasher) : base(accountVault, passwordHasher) { }

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

            if (account != null && _passwordHasher.Verify(usernamePasswordLoginRequest.Password, account.PasswordHash))
            {
                return account;
            }
        }

        return null;
    }

    public override async Task<AccountModel?> Signup(SignupRequest request)
    {
        if (request.Username == null)
        {
            throw new ArgumentException("Username must be provided.");
        }

        var account = new UsernamePasswordAccountVault
        {
            Username = request.Username,
            PasswordHash = _passwordHasher.Hash(request.Password),
        };

        await _accountVault.SaveAsync(account);
        return account;
    }
}