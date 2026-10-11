namespace Altruist.Security.Auth;

/// <summary>
/// Application-supplied account logic: checks credentials and creates accounts. Altruist ships no
/// implementation; register yours (e.g. <c>[Service(typeof(ILoginService))]</c>) and the auth controllers
/// (<see cref="Altruist.Security.Http.JwtAuthController"/>) call it from their login and signup endpoints.
/// </summary>
/// <example>
/// <code>
/// [Service(typeof(ILoginService))]
/// public sealed class MyLoginService : ILoginService
/// {
///     public async Task&lt;LoginResult&gt; LoginAsync(LoginRequest request) { /* look up, verify with IPasswordHasher */ }
///     public async Task&lt;SignupResult&gt; SignupAsync(SignupRequest request) { /* hash and store */ }
/// }
/// </code>
/// </example>
public interface ILoginService
{
    /// <summary>
    /// Verifies the credentials in <paramref name="request"/> (typically a <see cref="UsernamePasswordLoginRequest"/>
    /// or <see cref="EmailPasswordLoginRequest"/>). Return <see cref="LoginResult.ROk"/> with the account, or
    /// <see cref="LoginResult.RFailure"/>; the controllers treat a null <see cref="LoginResult.Model"/> as failure.
    /// </summary>
    Task<LoginResult> LoginAsync(LoginRequest request);
    /// <summary>
    /// Creates an account from <paramref name="request"/>. Return <see cref="SignupResult.ROk"/> with the new account
    /// (a null model is reported as failure) or <see cref="SignupResult.RFailure"/> with a client-facing error.
    /// </summary>
    Task<SignupResult> SignupAsync(SignupRequest request);
}

/// <summary>Outcome of <see cref="ILoginService.LoginAsync"/>. Create with <see cref="ROk"/> / <see cref="RFailure"/>.</summary>
public class LoginResult
{
    /// <summary>True when the credentials were accepted.</summary>
    public bool Success { get; }
    /// <summary>Why the login failed (null on success).</summary>
    public string? Error { get; }
    /// <summary>The signed-in account (null on failure).</summary>
    public AccountModel? Model { get; }

    /// <summary>Creates a result; prefer <see cref="ROk"/> / <see cref="RFailure"/>.</summary>
    /// <param name="success">Whether the login succeeded.</param>
    /// <param name="error">The failure reason, or null.</param>
    /// <param name="model">The account, or null.</param>
    public LoginResult(bool success, string? error, AccountModel? model) => (Success, Error, Model) = (success, error, model);

    /// <summary>A successful login for <paramref name="model"/>.</summary>
    public static LoginResult ROk(AccountModel? model) => new LoginResult(true, null, model);
    /// <summary>A failed login with <paramref name="error"/>.</summary>
    public static LoginResult RFailure(string error) => new LoginResult(false, error, null);
}

/// <summary>Outcome of <see cref="ILoginService.SignupAsync"/>. Create with <see cref="ROk"/> / <see cref="RFailure"/>.</summary>
public class SignupResult
{
    /// <summary>True when the account was created.</summary>
    public bool Success { get; }
    /// <summary>Why signup failed (null on success).</summary>
    public string? Error { get; }
    /// <summary>The created account (null on failure).</summary>
    public AccountModel? Model { get; }
    /// <summary>True when the account must confirm its email before it can sign in.</summary>
    public bool RequiresEmailVerification { get; }
    /// <summary>Details of the verification that was sent, when <see cref="RequiresEmailVerification"/>.</summary>
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

    /// <summary>A successful signup.</summary>
    /// <param name="model">The created account.</param>
    /// <param name="requiresEmailVerification">Whether the account must verify its email first.</param>
    /// <param name="verification">What was sent for verification, or null.</param>
    public static SignupResult ROk(
       AccountModel? model,
        bool requiresEmailVerification,
        VerificationInfo? verification)
        => new SignupResult(true, null, model, requiresEmailVerification, verification);

    /// <summary>A failed signup with <paramref name="error"/>.</summary>
    public static SignupResult RFailure(string error)
        => new SignupResult(false, error, null, false, null);
}

/// <summary>Where and how an account verification (e.g. an email link from an <see cref="IOneTimeTokenStore"/>) was sent.</summary>
public sealed class VerificationInfo
{
    /// <summary>Verification channel (default <c>"email"</c>).</summary>
    public string Method { get; init; } = "email";
    /// <summary>Address the verification was sent to.</summary>
    public string SentTo { get; init; } = default!;
    /// <summary>When the verification expires.</summary>
    public DateTimeOffset ExpiresAt { get; init; }
}
