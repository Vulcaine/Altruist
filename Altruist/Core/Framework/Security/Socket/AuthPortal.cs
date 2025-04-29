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

using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Altruist.Security;

public abstract class AuthPortal<TAuthContext> : Portal<PortalContext> where TAuthContext : ISessionAuthContext
{
    protected IIssuer Issuer;
    private readonly TokenSessionSyncService? _syncService;
    private readonly JwtTokenValidator _tokenValidator;


    protected AuthPortal(PortalContext context, ILoggerFactory loggerFactory, IIssuer issuer, IServiceProvider serviceProvider) : base(context, loggerFactory)
    {
        Issuer = issuer;
        _syncService = serviceProvider.GetService<TokenSessionSyncService>();
        _tokenValidator = serviceProvider.GetRequiredService<JwtTokenValidator>();
    }

    [Gate("upgrade")]
    public virtual async Task Upgrade(TAuthContext context, string clientId)
    {
        var connection = await GetConnectionAsync(clientId);
        if (connection != null)
        {
            var token = await UpgradeAuth(context, clientId);

            if (token != null)
            {
                // authorized close
                await Router.Client.SendAsync(clientId, token);
            }

            // unauthorized close
            await connection.CloseOutputAsync();
            await connection.CloseAsync();
        }
    }

    public virtual async Task<IIssue?> UpgradeAuth(TAuthContext context, string clientId)
    {
        var token = context.StatelessToken.Split(";")[0];
        var claims = _tokenValidator.ValidateToken(token);
        if (claims == null)
        {
            return null;
        }

        var groupKey = claims.FindFirst("GroupKey")?.Value;
        if (groupKey == null)
        {
            return null;
        }

        string? originalFingerprint = null;
        if (_syncService != null)
        {
            var old = await _syncService.DeleteAsync(context.StatelessToken, groupKey);

            if (old == null)
            {
                return null;
            }

            originalFingerprint = old.Fingerprint;
        }

        var newToken = Issuer.Issue();

        if (_syncService != null && newToken is TokenIssue tokenIssue)
        {
            var newAuthSession = new AuthTokenSessionModel
            {
                AccessToken = tokenIssue.AccessToken,
                AccessExpiration = tokenIssue.AccessExpiration,
                RefreshExpiration = tokenIssue.RefreshExpiration,
                RefreshToken = tokenIssue.RefreshToken,
                PrincipalId = claims.FindFirst(ClaimTypes.Name)?.Value!,
                Ip = claims.FindFirst("Ip")?.Value!,
                SysId = tokenIssue.AccessToken,
                Fingerprint = originalFingerprint
            };

            await _syncService.SaveAsync(newAuthSession, groupKey);
        }
        else
        {
            return null;
        }

        return newToken;
    }
}