using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace Altruist.Security;

[AttributeUsage(AttributeTargets.Method | AttributeTargets.Class, Inherited = true, AllowMultiple = true)]
public class ShieldAttribute : Attribute
{
    private readonly Type? _authHandlerType;

    public ShieldAttribute() { }

    public ShieldAttribute(Type authHandlerType)
    {
        _authHandlerType = authHandlerType;
    }

    // HTTP-based authentication
    public async Task OnAuthorizationAsync(AuthorizationFilterContext context)
    {
        var serviceProvider = context.HttpContext.RequestServices;
        if (_authHandlerType != null)
        {
            var authHandler = (IShieldAuth)serviceProvider.GetService(_authHandlerType)!;
            if (authHandler != null)
            {
                var result = await authHandler.HandleAuthAsync(new HttpAuthContext(context.HttpContext));
                context.HttpContext.Items["AuthResult"] = result;

                if (!result.AuthorizationResult.Succeeded)
                {
                    context.Result = new UnauthorizedResult();
                }
            }
        }
    }

    // Non-HTTP authentication (for TCP/UDP)
    public async Task<AuthDetails?> AuthenticateNonHttpAsync(IServiceProvider serviceProvider, IAuthContext context)
    {
        if (_authHandlerType != null)
        {
            var authHandler = (IShieldAuth)serviceProvider.GetService(_authHandlerType)!;
            if (authHandler != null)
            {
                var result = await authHandler.HandleAuthAsync(context);
                return result.AuthorizationResult.Succeeded ? result.AuthDetails : null;
            }
        }
        return null;
    }
}