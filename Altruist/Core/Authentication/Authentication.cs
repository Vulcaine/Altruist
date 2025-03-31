namespace Altruist.Authentication;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

[AttributeUsage(AttributeTargets.Method | AttributeTargets.Class, Inherited = true, AllowMultiple = true)]

public class ShieldAttribute : AuthorizeAttribute, IAsyncAuthorizationFilter
{
    private readonly Type? _authHandlerType;

    public ShieldAttribute() { }

    public ShieldAttribute(Type authHandlerType)
    {
        _authHandlerType = authHandlerType;
    }

    public async Task OnAuthorizationAsync(AuthorizationFilterContext context)
    {
        var serviceProvider = context.HttpContext.RequestServices;

        if (_authHandlerType != null)
        {
            var authHandler = (IShieldAuth)serviceProvider.GetService(_authHandlerType)!;
            if (authHandler != null)
            {
                var result = await authHandler.HandleAuthAsync(context.HttpContext);
                if (!result.Succeeded)
                {
                    context.Result = new UnauthorizedResult();
                    return;
                }
            }
        }

        await Task.CompletedTask;
    }
}
