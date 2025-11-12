namespace Altruist.Security.Auth;

public interface ILoginService
{
    Task<AccountModel> LoginAsync(LoginRequest request);
    Task<AccountModel> SignupAsync(SignupRequest request);
}