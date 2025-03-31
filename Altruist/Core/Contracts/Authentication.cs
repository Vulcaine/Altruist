using Microsoft.AspNetCore.Http;

namespace Altruist;

public interface IShield
{
    Task<bool> AuthenticateAsync(HttpContext context);
}
