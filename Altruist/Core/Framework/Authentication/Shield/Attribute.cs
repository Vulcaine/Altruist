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