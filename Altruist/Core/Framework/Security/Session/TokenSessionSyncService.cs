
using System.Collections.Concurrent;
using System.Net;

using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Logging;

namespace Altruist.Security;

/// <summary>
/// The <see cref="IShieldAuth"/> behind <see cref="SessionShieldAttribute"/> (registered with
/// <c>altruist:security:mode: session</c>). Looks the token up in <see cref="TokenSessionSyncService"/> (default group),
/// requires an unexpired access time and a matching client IP, and keeps a process-wide copy for
/// <see cref="AuthTokenSessionModel.CacheValidationInterval"/>. Each re-validation sets the access expiry to now plus that
/// interval, so a session idle for longer than the interval expires.
/// </summary>
[Service(typeof(IShieldAuth))]
[ConditionalOnConfig("altruist:security")]
[ConditionalOnConfig("altruist:security:mode", havingValue: "session")]
public class SessionTokenAuth : IShieldAuth
{
    private readonly TokenSessionSyncService _syncService;
    private readonly ILogger<SessionTokenAuth> _logger;
    private static readonly ConcurrentDictionary<string, CachedSession> _sessionCache = new();

    /// <summary>Creates the handler (resolved by DI).</summary>
    public SessionTokenAuth(TokenSessionSyncService syncService, ILogger<SessionTokenAuth> logger)
    {
        _syncService = syncService;
        _logger = logger;
    }


    /// <summary>Authenticates <see cref="IAuthContext.Token"/>; the returned details use the principal id as group key.</summary>
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
            // Cleanup expired session
            _sessionCache.TryRemove(session.AccessToken, out _);
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

