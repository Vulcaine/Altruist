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

    /// <summary>Creates the service (singleton via <c>[Service]</c>).</summary>
    /// <param name="cache">Primary session store.</param>
    /// <param name="vault">Optional persistent copy of the sessions.</param>
    public TokenSessionSyncService(ICacheProvider cache, IVault<AuthTokenSessionModel>? vault = null)
    {
        _cache = cache;
        _vault = vault;
    }

    /// <summary>The cached session with id <paramref name="id"/> in group <paramref name="cacheGroupId"/>, or null. Look up in the same group the session was saved under.</summary>
    public Task<AuthTokenSessionModel?> FindCachedByIdAsync(string id, string cacheGroupId = "")
        => _cache.GetAsync<AuthTokenSessionModel>(id, cacheGroupId);

    /// <summary>All cached sessions of group <paramref name="cacheGroupId"/>.</summary>
    public Task<ICursor<AuthTokenSessionModel>> FindAllCachedAsync(string cacheGroupId = "")
        => _cache.GetAllAsync<AuthTokenSessionModel>(cacheGroupId);

    /// <summary>Saves <paramref name="entity"/> in the cache under its <see cref="IVaultModel.StorageId"/> and group, and in the vault when present.</summary>
    public async Task SaveAsync(AuthTokenSessionModel entity, string cacheGroupId = "")
    {
        await _cache.SaveAsync(entity.StorageId, entity, cacheGroupId);
        if (_vault != null)
            await _vault.SaveAsync(entity);
    }

    /// <summary>
    /// Deletes a session and returns the removed cached copy (null when none). With a vault, the cache entry is only
    /// removed when the vault row existed.
    /// </summary>
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

/// <summary>
/// Requires a valid server-side session token (via <see cref="SessionTokenAuth"/>, registered with
/// <c>altruist:security:mode: session</c>); otherwise 401. The token is read from the context's <see cref="IAuthContext.Token"/>
/// (the raw <c>Authorization</c> header for HTTP) and the session must be bound to the caller's IP. For JWT mode use
/// <see cref="JwtShieldAttribute"/>; for browser WebSockets use <see cref="TicketShieldAttribute"/>.
/// </summary>
[AttributeUsage(AttributeTargets.Method | AttributeTargets.Class, Inherited = true, AllowMultiple = true)]
public class SessionShieldAttribute : ShieldAttribute
{
    /// <summary>Creates the shield.</summary>
    public SessionShieldAttribute() : base(typeof(SessionTokenAuth)) { }
}

/// <summary>Validates opaque session tokens by looking them up in <see cref="TokenSessionSyncService"/>.</summary>
public interface ISessionTokenValidator : ITokenValidator
{

}

/// <summary>Default <see cref="ISessionTokenValidator"/> (when <c>altruist:security</c> exists).</summary>
[Service(typeof(ISessionTokenValidator))]
[ConditionalOnConfig("altruist:security")]
public class SessionTokenValidator : ISessionTokenValidator
{
    private readonly TokenSessionSyncService _syncService;
    /// <summary>Creates the validator (resolved by DI).</summary>
    public SessionTokenValidator(TokenSessionSyncService syncService)
    {
        _syncService = syncService;
    }
    /// <summary>The principal of the session stored under <paramref name="token"/> (default cache group), or null. Does not check expiry.</summary>
    public async Task<ClaimsPrincipal?> ValidateToken(string token)
    {
        var cachedToken = await _syncService.FindCachedByIdAsync(token);
        if (cachedToken is null)
            return null;

        return ClaimsPrincipalFactory.Create(cachedToken);
    }
}

/// <summary>Builds principals from stored sessions.</summary>
public static class ClaimsPrincipalFactory
{
    /// <summary>A principal with a <see cref="ClaimTypes.NameIdentifier"/> claim of the session's principal id (authentication type <c>SessionToken</c>).</summary>
    /// <exception cref="ArgumentNullException">When <paramref name="cachedToken"/> is null.</exception>
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

