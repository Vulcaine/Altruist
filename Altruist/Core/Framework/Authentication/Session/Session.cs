using System.Collections.Concurrent;
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

        if (_sessionCache.TryGetValue(token, out var cached))
        {
            if (now - cached.LastValidatedAt < cached.SessionData.CacheValidationInterval)
            {
                if (cached.SessionData.ExpiresAt > now)
                    return Success(token, cached.SessionData);

                _sessionCache.TryRemove(token, out _);
                return Fail("Session expired (in-memory)");
            }
        }

        var session = await _cache.GetAsync<SessionData>(token);
        if (session == null)
        {
            _logger.LogWarning("Invalid session token: {Token}", token);
            _sessionCache.TryRemove(token, out _);
            return Fail("Session not found");
        }

        if (session.ExpiresAt < now)
        {
            _sessionCache.TryRemove(token, out _);
            return Fail("Session expired (cache)");
        }

        if (!Equals(session.Ip, context.ClientIp.ToString()))
        {
            _logger.LogWarning("IP mismatch for session {Token}", token);
            _sessionCache.TryRemove(token, out _);
            return Fail("IP mismatch");
        }

        // Refresh TTL (sliding expiration)
        session.ExpiresAt = now.Add(session.CacheValidationInterval);
        await _cache.SaveAsync(token, session);

        // Update in-memory cache
        _sessionCache[token] = new CachedSession
        {
            SessionData = session,
            LastValidatedAt = now
        };

        return Success(token, session);
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

