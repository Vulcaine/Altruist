using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;

namespace Altruist;

public interface ITokenValidator
{
    bool ValidateToken(string token);
}


public interface IShieldAuth
{
    Task<AuthorizationResult> HandleAuthAsync(HttpContext context);
}


public interface IShield
{
    Task<bool> AuthenticateAsync(HttpContext context);
}
