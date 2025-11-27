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

using System.Security.Claims;

using Altruist.Persistence;
namespace Altruist.Security;

[Service]
public class TokenSessionSyncService : AbstractVaultCacheSyncService<AuthTokenSessionModel>
{
    public TokenSessionSyncService(ICacheProvider cacheProvider, IVault<AuthTokenSessionModel>? vault = null) : base(cacheProvider, vault)
    {
    }
}

[AttributeUsage(AttributeTargets.Method | AttributeTargets.Class, Inherited = true, AllowMultiple = true)]
public class SessionShieldAttribute : ShieldAttribute
{
    public SessionShieldAttribute() : base(typeof(SessionTokenAuth)) { }
}

public interface ISessionTokenValidator : ITokenValidator
{

}

[Service(typeof(ISessionTokenValidator))]
public class SessionTokenValidator : ITokenValidator
{
    private readonly TokenSessionSyncService _syncService;
    public SessionTokenValidator(TokenSessionSyncService syncService)
    {
        _syncService = syncService;
    }
    public async Task<ClaimsPrincipal?> ValidateToken(string token)
    {
        var cachedToken = await _syncService.FindCachedByIdAsync(token);
        if (cachedToken is null)
            return null;

        return ClaimsPrincipalFactory.Create(cachedToken);
    }
}

public static class ClaimsPrincipalFactory
{
    public static ClaimsPrincipal Create(AuthTokenSessionModel cachedToken)
    {
        if (cachedToken is null)
            throw new ArgumentNullException(nameof(cachedToken));

        var principalId = cachedToken.PrincipalId.ToString();

        var claims = new List<Claim>
        {
            new Claim(ClaimTypes.NameIdentifier, principalId),
        };

        var identity = new ClaimsIdentity(claims, authenticationType: "SessionToken");
        return new ClaimsPrincipal(identity);
    }
}

