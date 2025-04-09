using System.Collections.Concurrent;
using System.Net;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Logging;

namespace Altruist.Authentication;

public class SessionTokenAuth : IShieldAuth
{
    private readonly ICacheProvider _cache;
    private readonly ILogger<SessionTokenAuth> _logger;
    private static readonly ConcurrentDictionary<string, CachedSession> _sessionCache = new();

    public SessionTokenAuth(ICacheProvider cache, ILogger<SessionTokenAuth> logger)
    {
        _cache = cache;
        _logger = logger;
    }

    public async Task<AuthResult> HandleAuthAsync(IAuthContext context)
    {
        var token = context.Token;
        if (string.IsNullOrWhiteSpace(token))
            return Fail("Missing session token");

        var now = DateTime.UtcNow;

        // Step 1: Check in-memory cache
        if (_sessionCache.TryGetValue(token, out var cached))
        {
            // Step 2: Validate cached session
            if (IsSessionValid(cached, now))
            {
                return Success(token, cached.SessionData);
            }

            _sessionCache.TryRemove(token, out _);
            return Fail("Session expired");
        }

        // Step 3: Fetch session from Redis
        var session = await GetSessionFromCache(token);
        if (session == null)
            return Fail("Session not found");

        // Step 4: Validate the session
        if (!ValidateSession(session, context.ClientIp, now))
        {
            _sessionCache.TryRemove(token, out _);
            return Fail("Session expired or IP mismatch");
        }

        // Step 5: Refresh TTL and update caches
        await RefreshSessionTtl(session, now);
        UpdateLocalCache(token, session, now);

        return Success(token, session);
    }

    private bool IsSessionValid(CachedSession cached, DateTime now)
    {
        return now - cached.LastValidatedAt < cached.SessionData.CacheValidationInterval
            && cached.SessionData.ExpiresAt > now;
    }

    private async Task<SessionData?> GetSessionFromCache(string token)
    {
        var session = await _cache.GetAsync<SessionData>(token);
        if (session == null)
        {
            _logger.LogWarning("Invalid session token: {Token}", token);
            _sessionCache.TryRemove(token, out _);
        }

        return session;
    }

    private bool ValidateSession(SessionData session, IPAddress clientIp, DateTime now)
    {
        if (session.ExpiresAt < now)
        {
            _sessionCache.TryRemove(session.Token, out _); // Cleanup expired session
            return false;
        }

        if (!Equals(session.Ip, clientIp.ToString()))
        {
            _logger.LogWarning("IP mismatch for session {Token}", session.Token);
            return false;
        }

        return true;
    }

    private async Task RefreshSessionTtl(SessionData session, DateTime now)
    {
        session.ExpiresAt = now.Add(session.CacheValidationInterval);
        await _cache.SaveAsync(session.Token, session);
    }

    private void UpdateLocalCache(string token, SessionData session, DateTime now)
    {
        _sessionCache[token] = new CachedSession
        {
            SessionData = session,
            LastValidatedAt = now
        };
    }

    private AuthResult Success(string token, SessionData session)
        => new(AuthorizationResult.Success(), new AuthDetails(token, session.ExpiresAt - DateTime.UtcNow));

    private AuthResult Fail(string reason)
    {
        _logger.LogDebug("Auth failed: {Reason}", reason);
        return new AuthResult(AuthorizationResult.Failed(), null!);
    }

    private class CachedSession
    {
        public SessionData SessionData { get; set; } = null!;
        public DateTime LastValidatedAt { get; set; }
    }
}


[AttributeUsage(AttributeTargets.Method | AttributeTargets.Class, Inherited = true, AllowMultiple = true)]
public class SessionShieldAttribute : ShieldAttribute
{
    public SessionShieldAttribute() : base(typeof(SessionTokenAuth)) { }
}

