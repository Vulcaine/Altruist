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

using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.DependencyInjection;

namespace Altruist.Security;

[AttributeUsage(AttributeTargets.Method | AttributeTargets.Class, Inherited = true, AllowMultiple = true)]
public class ShieldAttribute : Attribute, IAsyncAuthorizationFilter
{
    private readonly Type? _authHandlerType;

    public ShieldAttribute() { }

    public ShieldAttribute(Type authHandlerType)
    {
        _authHandlerType = authHandlerType;
    }

    // HTTP-based authentication for MVC + WebSockets. Fails closed: a handler that cannot be
    // resolved or that throws denies the request (previously both silently let it through).
    public async Task OnAuthorizationAsync(AuthorizationFilterContext context)
    {
        if (_authHandlerType is null)
            return;

        var authHandler = ResolveHandler(context.HttpContext.RequestServices);
        if (authHandler is null)
        {
            context.Result = new UnauthorizedResult();
            return;
        }

        try
        {
            var result = await authHandler.HandleAuthAsync(new HttpAuthContext(context.HttpContext));

            context.HttpContext.Items["AuthResult"] = result;

            if (!result.AuthorizationResult.Succeeded)
            {
                context.Result = new UnauthorizedResult();
            }
        }
        catch (Exception)
        {
            context.HttpContext.Items.Remove("AuthResult");
            context.Result = new UnauthorizedResult();
        }
    }

    private IShieldAuth? ResolveHandler(IServiceProvider serviceProvider)
    {
        if (serviceProvider.GetService(_authHandlerType!) is IShieldAuth registered)
            return registered;

        // Handlers are usually registered under IShieldAuth only; build the concrete type.
        try
        {
            return ActivatorUtilities.CreateInstance(serviceProvider, _authHandlerType!) as IShieldAuth;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    // Non-HTTP authentication (for TCP/UDP)
    public async Task<AuthDetails?> AuthenticateNonHttpAsync(IServiceProvider serviceProvider, IAuthContext context)
    {
        if (_authHandlerType != null)
        {
            var authHandler = ResolveHandler(serviceProvider);
            if (authHandler != null)
            {
                try
                {
                    var result = await authHandler.HandleAuthAsync(context);
                    return result.AuthorizationResult.Succeeded ? result.AuthDetails : null;
                }
                catch (Exception)
                {
                    return null;
                }
            }
        }
        return null;
    }
}
