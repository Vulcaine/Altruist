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

using Altruist.Security.Auth;

using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Altruist.Security;

/// <summary>
/// An abstract base controller that provides a structured authentication flow using tokens (JWT or session-based).
/// It supports login, refresh, session tracking, and extensibility for custom authentication logic.
/// </summary>
/// <remarks>
/// This controller handles:
/// - Logging in with username and password.
/// - Issuing tokens via an <see cref="IIssuer"/> (e.g., <see cref="JwtTokenIssuer"/>).
/// - Refreshing tokens using a session-based system via <see cref="TokenSessionSyncService"/>.
/// - Invalidating and saving user sessions per IP.
/// </remarks>
public abstract class AuthController : ControllerBase
{
    /// <summary>The token issuer the controller signs in with (an <see cref="IJwtTokenIssuer"/> for <see cref="Altruist.Security.Http.JwtAuthController"/>).</summary>
    protected readonly IIssuer _issuer;
    /// <summary>Session store; null disables session tracking (the protected helpers then do nothing).</summary>
    protected readonly TokenSessionSyncService? _syncService;

    /// <summary>The application's credential check and account creation.</summary>
    protected readonly ILoginService _loginService;

    /// <summary>Logger for the auth flow.</summary>
    protected readonly ILogger<AuthController> _logger;

    /// <summary>Creates the controller (derived controllers are resolved by MVC).</summary>
    /// <param name="issuer">Token issuer.</param>
    /// <param name="loginService">Application login/signup logic.</param>
    /// <param name="tokenSessionSyncService">Session store.</param>
    /// <param name="loggerFactory">Logger factory.</param>
    protected AuthController(IIssuer issuer, ILoginService loginService, TokenSessionSyncService tokenSessionSyncService, ILoggerFactory loggerFactory)
    {
        _issuer = issuer;
        _loginService = loginService;
        _syncService = tokenSessionSyncService;
        _logger = loggerFactory.CreateLogger<AuthController>();
    }

    /// <summary>Deletes every cached session of <paramref name="groupKey"/> (sign out everywhere).</summary>
    protected async Task InvalidateAllSessions(string groupKey)
    {
        if (_syncService != null)
        {
            var cursor = await _syncService.FindAllCachedAsync(groupKey);
            foreach (var session in cursor)
            {
                await _syncService.DeleteAsync(session.StorageId, groupKey);
                _logger.LogInformation($"[auth][{groupKey}] ✅ Invalidated session: {session.StorageId}");
            }
        }
    }


    /// <summary>Deletes the sessions of <paramref name="groupKey"/> whose access and refresh tokens have both expired.</summary>
    protected async Task InvalidateExpiredSessions(string groupKey)
    {
        if (_syncService != null)
        {
            var cursor = await _syncService.FindAllCachedAsync(groupKey);
            foreach (var session in cursor)
            {
                if (!session.IsAccessTokenValid() && !session.IsRefreshTokenValid())
                {
                    await _syncService.DeleteAsync(session.StorageId, groupKey);
                    _logger.LogInformation($"[auth][{groupKey}] ✅ Invalidated expired session: {session.StorageId}");
                }
            }
        }
    }

    /// <summary>
    /// Stores <paramref name="issue"/> as an <see cref="AuthTokenSessionModel"/> keyed by its access token and bound
    /// to the caller's IP, replacing all other sessions of <paramref name="groupKey"/> (one session per group).
    /// Returns false only when the request has no remote IP; does nothing (true) when <paramref name="issue"/> is null
    /// or no session store is registered.
    /// </summary>
    /// <param name="issue">The tokens just issued.</param>
    /// <param name="groupKey">Session group, from <see cref="SessionGroupKeyStrategy"/>.</param>
    /// <param name="principal">The principal id.</param>
    /// <param name="fingerprint">Optional client fingerprint the session is bound to.</param>
    protected async Task<bool> CreateAndSaveAuthSessionAsync(TokenIssue? issue, string groupKey, string principal, string? fingerprint = null)
    {
        if (issue != null && _syncService != null)
        {
            var ip = HttpContext.Connection.RemoteIpAddress?.ToString();

            if (ip == null)
            {
                _logger.LogWarning($"[auth][{groupKey}] ❌ Login rejected – missing IP address for principal: {principal}");

                return false;
            }

            var authData = new AuthTokenSessionModel
            {
                AccessToken = issue.AccessToken,
                AccessExpiration = issue.AccessExpiration,
                RefreshExpiration = issue.RefreshExpiration,
                RefreshToken = issue.RefreshToken,
                PrincipalId = principal,
                Ip = ip,
                StorageId = issue.AccessToken,
                Fingerprint = fingerprint
            };

            await SaveAuthSessionAsync(authData, groupKey);
            _logger.LogInformation($"[auth][{groupKey}] ✅ Auth session created for principal: {principal}, IP: {ip}");

        }

        return true;
    }

    /// <summary>Invalidates all sessions of <paramref name="groupKey"/>, then saves <paramref name="session"/> under it.</summary>
    protected async Task SaveAuthSessionAsync(AuthTokenSessionModel session, string groupKey)
    {
        if (_syncService != null)
        {
            await InvalidateAllSessions(groupKey);
            await _syncService.SaveAsync(session, groupKey);
            _logger.LogInformation($"[auth][{groupKey}] 💾 Session saved: {session.StorageId} (principal: {session.PrincipalId})");
        }
    }

    /// <summary>
    /// Returns a key used to group all sessions for a principal (e.g., user).
    /// Useful for controlling concurrent session behavior or targeting specific session invalidations.
    /// </summary>
    protected virtual string SessionGroupKeyStrategy(string principalId) => principalId;
}
