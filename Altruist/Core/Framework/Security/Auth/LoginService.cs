namespace Altruist.Security.Auth;

public interface ILoginService
{
    Task<LoginResult> LoginAsync(LoginRequest request);
    Task<SignupResult> SignupAsync(SignupRequest request);
}

public class LoginResult
{
    public bool Success { get; }
    public string? Error { get; }
    public AccountModel? Model { get; }

    public LoginResult(bool success, string? error, AccountModel? model) => (Success, Error, Model) = (success, error, model);

    public static LoginResult ROk(AccountModel? model) => new LoginResult(true, null, model);
    public static LoginResult RFailure(string error) => new LoginResult(false, error, null);
}

public class SignupResult
{
    public bool Success { get; }
    public string? Error { get; }
    public AccountModel? Model { get; }
    public bool RequiresEmailVerification { get; }
    public VerificationInfo? Verification { get; }

    private SignupResult(
        bool success,
        string? error,
        AccountModel? model,
        bool requiresEmailVerification,
        VerificationInfo? verification)
    {
        Success = success;
        Error = error;
        Model = model;
        RequiresEmailVerification = requiresEmailVerification;
        Verification = verification;
    }

    public static SignupResult ROk(
       AccountModel? model,
        bool requiresEmailVerification,
        VerificationInfo? verification)
        => new SignupResult(true, null, model, requiresEmailVerification, verification);

    public static SignupResult RFailure(string error)
        => new SignupResult(false, error, null, false, null);
}

public sealed class VerificationInfo
{
    public string Method { get; init; } = "email";
    public string SentTo { get; init; } = default!;
    public DateTimeOffset ExpiresAt { get; init; }
}