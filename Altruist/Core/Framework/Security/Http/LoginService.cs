/* 
Copyright 2025 Aron Gere

Licensed under the Apache License, Version 2.0 (the "License");
you may not use this file except in compliance with the License.
You may obtain a copy of the License at

    http://www.apache.org/licenses/LICENSE-2.0

Unless required by applicable law or agreed to in writing, software
distributed under the License is distributed on an "AS IS" BASIS,
WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
See the License for the specific language governing permissions and
limitations under the License.
*/

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

public class UsernamePasswordLoginService<TAccount> : LoginService<TAccount> where TAccount : UsernamePasswordAccountModel
{
    public UsernamePasswordLoginService(IVault<TAccount> accountVault, IPasswordHasher passwordHasher) : base(accountVault, passwordHasher) { }

    public override async Task<AccountModel?> FindAccountByIdAsync(string id)
    {
        return await _accountVault.Where(acc => acc.SysId == id).FirstOrDefaultAsync();
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

        var account = new UsernamePasswordAccountModel
        {
            Username = request.Username,
            PasswordHash = _passwordHasher.Hash(request.Password),
        };

        await _accountVault.SaveAsync(account);
        return account;
    }
}