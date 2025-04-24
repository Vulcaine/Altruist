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

using System.Net;
using System.Security.Claims;
using Altruist.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;

namespace Altruist;

public class AuthResult
{
    public AuthorizationResult AuthorizationResult { get; }
    public AuthDetails? AuthDetails { get; }

    public AuthResult(AuthorizationResult authorizationResult, AuthDetails? authDetails)
    {
        AuthorizationResult = authorizationResult;
        AuthDetails = authDetails;
    }
}

public interface ITokenValidator
{
    ClaimsPrincipal? ValidateToken(string token);
}


public interface IAuthContext
{
    public string ClientId { get; set; }
    public string? Token { get; set; }
    public IPAddress ClientIp { get; set; }
    public DateTime ConnectionTimestamp { get; set; }
}

public class SocketAuthContext : IAuthContext
{
    public string ClientId { get; set; } = string.Empty;
    public string? Token { get; set; } = string.Empty;
    public IPAddress ClientIp { get; set; } = IPAddress.None;
    public DateTime ConnectionTimestamp { get; set; }

}

public class HttpAuthContext : IAuthContext
{
    public string ClientId { get; set; } = string.Empty;
    public string? Token { get; set; } = string.Empty;
    public IPAddress ClientIp { get; set; } = IPAddress.None;
    public DateTime ConnectionTimestamp { get; set; }

    public HttpContext HttpContext { get; set; }

    public HttpAuthContext(HttpContext httpContext)
    {
        HttpContext = httpContext;

        ClientId = httpContext.Request.Headers["ClientId"]!;
        Token = httpContext.Request.Headers["Authorization"]!; // Assuming token is in Authorization header

        ClientIp = httpContext.Connection.RemoteIpAddress ?? IPAddress.None;
        ConnectionTimestamp = DateTime.UtcNow;
    }
}



public interface IShieldAuth
{
    Task<AuthResult> HandleAuthAsync(IAuthContext context);
}

public interface IShield
{
    Task<bool> AuthenticateAsync(HttpContext context);
}
