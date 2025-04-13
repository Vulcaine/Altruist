using System.Collections.Concurrent;
using System.Net;
using Altruist.Database;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Logging;

namespace Altruist.Authentication;

public class TokenSessionSyncService : AbstractVaultCacheSyncService<AuthSessionData>
{
    public TokenSessionSyncService(ICacheProvider cacheProvider, IVault<AuthSessionData>? vault = null) : base(cacheProvider, vault)
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
            && cached.SessionData.Expiration > now;
    }

    private async Task<AuthSessionData?> GetSessionFromCache(string token)
    {
        var session = await _syncService.FindCachedByIdAsync(token);
        if (session == null)
        {
            _logger.LogWarning("Invalid session token: {Token}", token);
            _sessionCache.TryRemove(token, out _);
        }

        return session;
    }

    private bool ValidateSession(AuthSessionData session, IPAddress clientIp, DateTime now)
    {
        if (session.Expiration < now)
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

    private async Task RefreshSessionTtl(AuthSessionData session, DateTime now)
    {
        session.Expiration = now.Add(session.CacheValidationInterval);
        await _syncService.SaveAsync(session);
    }

    private void UpdateLocalCache(string token, AuthSessionData session, DateTime now)
    {
        _sessionCache[token] = new CachedSession
        {
            SessionData = session,
            LastValidatedAt = now
        };
    }

    private AuthResult Success(string token, AuthSessionData session)
        => new(AuthorizationResult.Success(), new AuthDetails(token, session.Expiration - DateTime.UtcNow));

    private AuthResult Fail(string reason)
    {
        _logger.LogDebug("Auth failed: {Reason}", reason);
        return new AuthResult(AuthorizationResult.Failed(), null!);
    }

    private class CachedSession
    {
        public AuthSessionData SessionData { get; set; } = null!;
        public DateTime LastValidatedAt { get; set; }
    }
}


[AttributeUsage(AttributeTargets.Method | AttributeTargets.Class, Inherited = true, AllowMultiple = true)]
public class SessionShieldAttribute : ShieldAttribute
{
    public SessionShieldAttribute() : base(typeof(SessionTokenAuth)) { }
}

