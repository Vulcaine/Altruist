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

/// <summary>
/// Session-token cache wrapper. Reads/writes <see cref="AuthTokenSessionModel"/>
/// against the in-memory <see cref="ICacheProvider"/>, dual-writing to the optional
/// vault when configured.
/// </summary>
[Service]
[ConditionalOnConfig("altruist:security")]
public class TokenSessionSyncService
{
    private readonly ICacheProvider _cache;
    private readonly IVault<AuthTokenSessionModel>? _vault;

    public TokenSessionSyncService(ICacheProvider cache, IVault<AuthTokenSessionModel>? vault = null)
    {
        _cache = cache;
        _vault = vault;
    }

    public Task<AuthTokenSessionModel?> FindCachedByIdAsync(string id, string cacheGroupId = "")
        => _cache.GetAsync<AuthTokenSessionModel>(id, cacheGroupId);

    public Task<ICursor<AuthTokenSessionModel>> FindAllCachedAsync(string cacheGroupId = "")
        => _cache.GetAllAsync<AuthTokenSessionModel>(cacheGroupId);

    public async Task SaveAsync(AuthTokenSessionModel entity, string cacheGroupId = "")
    {
        await _cache.SaveAsync(entity.StorageId, entity, cacheGroupId);
        if (_vault != null)
            await _vault.SaveAsync(entity);
    }

    public async Task<AuthTokenSessionModel?> DeleteAsync(string id, string cacheGroupId = "")
    {
        if (_vault == null)
            return await _cache.RemoveAsync<AuthTokenSessionModel>(id, cacheGroupId);

        var deleted = await _vault.Where(x => x.StorageId == id).DeleteAsync();
        if (deleted)
            return await _cache.RemoveAsync<AuthTokenSessionModel>(id, cacheGroupId);
        return null;
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
[ConditionalOnConfig("altruist:security")]
public class SessionTokenValidator : ISessionTokenValidator
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

