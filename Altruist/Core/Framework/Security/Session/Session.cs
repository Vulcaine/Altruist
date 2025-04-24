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

using System.Collections.Concurrent;
using System.Net;
using Altruist.Database;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Logging;

namespace Altruist.Security;

public class TokenSessionSyncService : AbstractVaultCacheSyncService<AuthTokenSessionModel>
{
    public TokenSessionSyncService(ICacheProvider cacheProvider, IVault<AuthTokenSessionModel>? vault = null) : base(cacheProvider, vault)
    {
    }
}

public class SessionTokenAuth : IShieldAuth
{
    private readonly TokenSessionSyncService _syncService;
    private readonly ILogger<SessionTokenAuth> _logger;
    private static readonly ConcurrentDictionary<string, CachedSession> _sessionCache = new();

    public SessionTokenAuth(TokenSessionSyncService syncService, ILogger<SessionTokenAuth> logger)
    {
        _syncService = syncService;
        _logger = logger;
    }

    public async Task<AuthResult> HandleAuthAsync(IAuthContext context)
    {
        var token = context.Token;
        if (string.IsNullOrWhiteSpace(token))
            return Fail("Missing session token");

        var now = DateTime.UtcNow;

        if (_sessionCache.TryGetValue(token, out var cached))
        {
            if (IsSessionValid(cached, now))
            {
                return Success(token, cached.SessionData);
            }

            _sessionCache.TryRemove(token, out _);
            return Fail("Session expired");
        }

        var session = await GetSessionFromCache(token);
        if (session == null)
            return Fail("Session not found");

        if (!ValidateSession(session, context.ClientIp, now))
        {
            _sessionCache.TryRemove(token, out _);
            return Fail("Session expired or IP mismatch");
        }

        await RefreshSessionTtl(session, now);
        UpdateLocalCache(token, session, now);

        return Success(token, session);
    }

    private bool IsSessionValid(CachedSession cached, DateTime now)
    {
        return now - cached.LastValidatedAt < cached.SessionData.CacheValidationInterval
            && cached.SessionData.AccessExpiration > now;
    }

    private async Task<AuthTokenSessionModel?> GetSessionFromCache(string token)
    {
        var session = await _syncService.FindCachedByIdAsync(token);
        if (session == null)
        {
            _logger.LogWarning("Invalid session token: {Token}", token);
            _sessionCache.TryRemove(token, out _);
        }

        return session;
    }

    private bool ValidateSession(AuthTokenSessionModel session, IPAddress clientIp, DateTime now)
    {
        if (session.AccessExpiration < now)
        {
            _sessionCache.TryRemove(session.AccessToken, out _); // Cleanup expired session
            return false;
        }

        if (!Equals(session.Ip, clientIp.ToString()))
        {
            _logger.LogWarning("IP mismatch for session {Token}", session.AccessToken);
            return false;
        }

        return true;
    }

    private async Task RefreshSessionTtl(AuthTokenSessionModel session, DateTime now)
    {
        session.AccessExpiration = now.Add(session.CacheValidationInterval);
        await _syncService.SaveAsync(session);
    }

    private void UpdateLocalCache(string token, AuthTokenSessionModel session, DateTime now)
    {
        _sessionCache[token] = new CachedSession
        {
            SessionData = session,
            LastValidatedAt = now
        };
    }

    private AuthResult Success(string token, AuthTokenSessionModel session)
    {
        // TODO: currently we are assigning PrincipalID as groupkey which might not be good always
        return new(AuthorizationResult.Success(), new AuthDetails(token, session.PrincipalId, session.Ip, session.PrincipalId, session.AccessExpiration - DateTime.UtcNow));

    }
    private AuthResult Fail(string reason)
    {
        _logger.LogDebug("Auth failed: {Reason}", reason);
        return new AuthResult(AuthorizationResult.Failed(), null!);
    }

    private class CachedSession
    {
        public AuthTokenSessionModel SessionData { get; set; } = null!;
        public DateTime LastValidatedAt { get; set; }
    }
}


[AttributeUsage(AttributeTargets.Method | AttributeTargets.Class, Inherited = true, AllowMultiple = true)]
public class SessionShieldAttribute : ShieldAttribute
{
    public SessionShieldAttribute() : base(typeof(SessionTokenAuth)) { }
}

