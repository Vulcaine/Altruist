using Altruist.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;

namespace Altruist;

public class AuthResult {
    public AuthorizationResult AuthorizationResult { get; }
    public AuthDetails? AuthDetails { get; }

    public AuthResult(AuthorizationResult authorizationResult, AuthDetails? authDetails){
        AuthorizationResult = authorizationResult;
        AuthDetails = authDetails;
    }
}

public interface ITokenValidator
{
    bool ValidateToken(string token);
}


public interface IShieldAuth
{
    Task<AuthResult> HandleAuthAsync(HttpContext context);
}


public interface IShield
{
    Task<bool> AuthenticateAsync(HttpContext context);
}
