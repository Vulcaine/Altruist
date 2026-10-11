
using System.Collections.Concurrent;
using System.Net;

using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Logging;

namespace Altruist.Security;

/// <summary>
/// The <see cref="IShieldAuth"/> behind <see cref="SessionShieldAttribute"/> (registered with
/// <c>altruist:security:mode: session</c>). Looks the token up with <see cref="TokenSessionSyncService.FindByTokenAsync"/>
/// (in whichever group it was saved under), requires an unexpired access time and a matching client IP, and keeps a
/// process-wide copy for <see cref="AuthTokenSessionModel.CacheValidationInterval"/> before reading the store again.
/// A session stays valid until its issued <see cref="AuthTokenSessionModel.AccessExpiration"/>; validation never
/// changes it.
/// </summary>
[Service(typeof(IShieldAuth))]
[ConditionalOnConfig("altruist:security")]
[ConditionalOnConfig("altruist:security:mode", havingValue: "session")]
public class SessionTokenAuth : IShieldAuth
{
    private readonly TokenSessionSyncService _syncService;
    private readonly ILogger<SessionTokenAuth> _logger;
    // Static: the shield may build a new handler per request, and the copy must outlive it.
    private static readonly ConcurrentDictionary<string, CachedSession> _sessionCache = new();

    /// <summary>Creates the handler (resolved by DI).</summary>
    public SessionTokenAuth(TokenSessionSyncService syncService, ILogger<SessionTokenAuth> logger)
    {
        _syncService = syncService;
        _logger = logger;
    }

    /// <summary>
    /// Authenticates <see cref="IAuthContext.Token"/>; the returned details carry the session's principal, IP, the group
    /// it is stored under and its remaining access time.
    /// </summary>
    public async Task<AuthResult> HandleAuthAsync(IAuthContext context)
    {
        var token = context.Token;
        if (string.IsNullOrWhiteSpace(token))
            return Fail("Missing session token");

        var now = DateTime.UtcNow;

        if (_sessionCache.TryGetValue(token, out var cached) && now - cached.LastValidatedAt < cached.Session.CacheValidationInterval)
        {
            if (!ValidateSession(cached.Session, context.ClientIp, now))
                return Fail("Session expired or IP mismatch");
            return Success(token, cached.Session, cached.GroupKey);
        }

        _sessionCache.TryRemove(token, out _);
        var found = await _syncService.FindByTokenAsync(token);
        if (found == null)
            return Fail("Session not found");

        if (!ValidateSession(found.Session, context.ClientIp, now))
            return Fail("Session expired or IP mismatch");

        _sessionCache[token] = new CachedSession(found.Session, found.GroupKey, now);
        return Success(token, found.Session, found.GroupKey);
    }

    private bool ValidateSession(AuthTokenSessionModel session, IPAddress clientIp, DateTime now)
    {
        if (session.AccessExpiration <= now)
            return false;

        if (!Equals(session.Ip, clientIp.ToString()))
        {
            _logger.LogWarning("IP mismatch for the session of principal {Principal}", session.PrincipalId);
            return false;
        }

        return true;
    }

    private static AuthResult Success(string token, AuthTokenSessionModel session, string groupKey) =>
        new(AuthorizationResult.Success(), new AuthDetails(token, session.PrincipalId, session.Ip,
            string.IsNullOrEmpty(groupKey) ? session.PrincipalId : groupKey, session.AccessExpiration - DateTime.UtcNow));

    private AuthResult Fail(string reason)
    {
        _logger.LogDebug("Auth failed: {Reason}", reason);
        return new AuthResult(AuthorizationResult.Failed(), null!);
    }

    private sealed record CachedSession(AuthTokenSessionModel Session, string GroupKey, DateTime LastValidatedAt);
}
