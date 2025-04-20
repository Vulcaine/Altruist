using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Tokens;

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
    protected readonly ILoginService _loginService;
    protected readonly IIssuer _issuer;
    protected readonly TokenSessionSyncService? _syncService;

    protected readonly ILogger<AuthController> _logger;

    protected AuthController(IIssuer issuer, ILoggerFactory loggerFactory, IServiceProvider serviceProvider)
    {
        _loginService = LoginService(serviceProvider);
        _issuer = issuer;
        _syncService = serviceProvider.GetService<TokenSessionSyncService>();
        _logger = loggerFactory.CreateLogger<AuthController>();
    }

    protected async Task InvalidateAllSessions(string groupKey)
    {
        if (_syncService != null)
        {
            var cursor = await _syncService.FindAllCachedAsync(groupKey);
            foreach (var session in cursor)
            {
                await _syncService.DeleteAsync(session.GenId, groupKey);
                _logger.LogInformation($"[auth][{groupKey}] ✅ Invalidated session: {session.GenId}");
            }
        }
    }


    protected async Task InvalidateExpiredSessions(string groupKey)
    {
        if (_syncService != null)
        {
            var cursor = await _syncService.FindAllCachedAsync(groupKey);
            foreach (var session in cursor)
            {
                if (!session.IsAccessTokenValid() && !session.IsRefreshTokenValid())
                {
                    await _syncService.DeleteAsync(session.GenId, groupKey);
                    _logger.LogInformation($"[auth][{groupKey}] ✅ Invalidated expired session: {session.GenId}");
                }
            }
        }
    }

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
                GenId = issue.AccessToken,
                Fingerprint = fingerprint
            };

            await SaveAuthSessionAsync(authData, groupKey);
            _logger.LogInformation($"[auth][{groupKey}] ✅ Auth session created for principal: {principal}, IP: {ip}");

        }

        return true;
    }

    protected async Task SaveAuthSessionAsync(AuthTokenSessionModel session, string groupKey)
    {
        if (_syncService != null)
        {
            await InvalidateAllSessions(groupKey);
            await _syncService.SaveAsync(session, groupKey);
            _logger.LogInformation($"[auth][{groupKey}] 💾 Session saved: {session.GenId} (principal: {session.PrincipalId})");
        }
    }

    /// <summary>
    /// Returns a key used to group all sessions for a principal (e.g., user).
    /// Useful for controlling concurrent session behavior or targeting specific session invalidations.
    /// </summary>
    protected virtual string SessionGroupKeyStrategy(string principalId) => principalId;


    /// <summary>
    /// Provides the login service instance to handle username/password authentication.
    /// Must be implemented in derived classes.
    /// </summary>
    protected abstract ILoginService LoginService(IServiceProvider serviceProvider);
}

/// <summary>
/// Extends <see cref="AuthController"/> to implement JWT-specific login and refresh handling.
/// </summary>
public abstract class JwtAuthController : AuthController
{
    protected readonly JwtTokenValidator _tokenValidator;
    protected JwtAuthController(IIssuer issuer,
    ILoggerFactory loggerFactory, IServiceProvider serviceProvider)
        : base(issuer, loggerFactory, serviceProvider)
    {
        _tokenValidator = serviceProvider.GetRequiredService<JwtTokenValidator>();
    }

    [HttpPost("signup")]
    public async Task<IActionResult> Signup([FromBody] SignupRequest request)
    {
        var account = await _loginService.Signup(request);

        if (account != null)
        {
            _logger.LogInformation($"[signup][{request.Email}] ✅ Signup succeeded – user ID: {account.GenId}");

            return Ok();
        }
        else
        {
            _logger.LogWarning($"[signup][{request.Email}] ❌ Signup failed");
            return BadRequest();
        }
    }

    [HttpPost("login/emailpwd")]
    public async Task<IActionResult> EmailPasswordLogin([FromBody] EmailPasswordLoginRequest request)
    {
        try
        {
            var account = await _loginService.Login(request);
            if (account == null)
            {
                _logger.LogWarning($"[login-email][{request.Email}] ❌ Login failed");
                return Unauthorized();
            }

            var claims = GetClaimsForLogin(account, request);
            var issue = IssueToken(claims);
            var groupKey = SessionGroupKeyStrategy(account.GenId);

            if (!await CreateAndSaveAuthSessionAsync(issue, groupKey, account.GenId, request.Fingerprint))
                return Unauthorized($"[login-email][${request.Email}] Only clients with IP address are allowed to connect.");

            _logger.LogInformation($"[login-email][{groupKey}] ✅ Login succeeded (email: {request.Email})");
            return OkOrUnauthorized(issue);
        }
        catch
        {
            return Unauthorized();
        }
    }

    [HttpPost("login/unamepwd")]
    public async Task<IActionResult> UsernamePasswordLogin([FromBody] UsernamePasswordLoginRequest request)
    {
        try
        {
            var account = await _loginService.Login(request);
            if (account == null)
            {
                _logger.LogWarning($"[login-uname][{request.Username}] ❌ Login failed");
                return Unauthorized();
            }

            var claims = GetClaimsForLogin(account, request);
            var issue = IssueToken(claims);
            var groupKey = SessionGroupKeyStrategy(account.GenId);

            if (!await CreateAndSaveAuthSessionAsync(issue, groupKey, account.GenId, request.Fingerprint))
                return Unauthorized($"[login-uname][${request.Username}] Only clients with IP address are allowed to connect.");

            _logger.LogInformation($"[login-uname][{groupKey}] ✅ Login succeeded (username: {request.Username})");
            return OkOrUnauthorized(issue);
        }
        catch
        {
            return Unauthorized();
        }
    }

    [HttpPost("refresh")]
    public async Task<IActionResult> Refresh()
    {
        if (_syncService == null)
            throw new InvalidOperationException("TokenSessionSyncService is not registered. Did you forget to call .StatefulToken()?");

        var authHeader = HttpContext.Request.Headers["Authorization"].ToString();
        if (string.IsNullOrWhiteSpace(authHeader) || !authHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            return Unauthorized("Expected format: Bearer <access_token>;<jwt>;<refresh_token>;<jwt>");

        var parts = authHeader["Bearer ".Length..].Trim().Split(';');
        if (parts.Length != 4)
            return Unauthorized("Expected format: <access_token>;<access_protocol>;<refresh_token>;<refresh_protocol>");

        var accessToken = parts[0];
        var accessProtocol = parts[1].ToLowerInvariant();
        var refreshToken = parts[2];
        var refreshProtocol = parts[3].ToLowerInvariant();

        if (accessProtocol != "jwt" || refreshProtocol != "jwt")
            return Unauthorized("This endpoint only supports JWT access and refresh tokens.");

        try
        {
            var claims = _tokenValidator.GetClaimsPrincipal(accessToken);
            if (claims == null)
            {
                _logger.LogWarning($"[refresh] ❌ Invalid access token during refresh");
                return Unauthorized("Invalid access token.");
            }

            string? fingerprint = claims.FindFirst("Fingerprint")?.Value;
            var groupKey = claims.FindFirst("GroupKey")?.Value;

            if (groupKey == null)
            {
                _logger.LogWarning($"[refresh] ❌ Invalid access token during refresh");
                return Unauthorized("Invalid access token.");
            }

            var accessKey = $"{accessToken};jwt";
            var cached = await _syncService.FindCachedByIdAsync(accessKey, groupKey ?? "");

            if (cached?.IsRefreshTokenValid() != true || cached.Fingerprint != fingerprint)
            {
                _logger.LogWarning($"[refresh] ❌ Invalid/expired session for refresh token: {refreshToken}");
                return Unauthorized("Invalid or expired session.");
            }

            var expectedRefreshKey = $"{cached.RefreshToken}";
            var providedRefreshKey = $"{refreshToken};jwt";

            if (!string.Equals(providedRefreshKey, expectedRefreshKey, StringComparison.Ordinal))
            {
                _logger.LogWarning($"[refresh][{cached.PrincipalId}] ❌ Refresh token mismatch.");
                return Unauthorized("Refresh token mismatch.");
            }

            if (_issuer is not JwtTokenIssuer jwtIssuer)
                return Unauthorized("JWT issuer not configured.");

            var principal = GetPrincipalFromToken(accessToken, jwtIssuer.JwtOptions.TokenValidationParameters);
            if (principal == null)
            {
                _logger.LogWarning($"[refresh] ❌ Invalid access token during refresh");
                return Unauthorized("Invalid access token.");
            }

            var issue = IssueToken(principal.Claims);

            if (!await CreateAndSaveAuthSessionAsync(issue, groupKey!, cached.PrincipalId, cached.Fingerprint))
                return Unauthorized("Couldn't identify client IP.");

            _logger.LogInformation($"[refresh][{groupKey}] 🔁 Token refreshed (principal: {cached.PrincipalId})");
            return OkOrUnauthorized(issue);
        }
        catch (Exception ex)
        {
            _logger.LogError($"[refresh] Exception: {ex.Message}");
            return Unauthorized();
        }
    }

    private IActionResult OkOrUnauthorized(TokenIssue? token) => token is null
        ? Unauthorized()
        : Ok(new AltruistLoginResponse
        {
            AccessToken = token.AccessToken,
            RefreshToken = token.RefreshToken
        });

    private TokenIssue? IssueToken(IEnumerable<Claim> claims)
    {
        if (_issuer is not JwtTokenIssuer jwtIssuer)
            return null;

        return jwtIssuer.WithClaims(claims).Issue() as TokenIssue;
    }

    private ClaimsPrincipal? GetPrincipalFromToken(string token, TokenValidationParameters validation)
    {
        try
        {
            var handler = new JwtSecurityTokenHandler();
            return handler.ValidateToken(token, validation, out _);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Generates the set of claims to be included in the token upon successful login.
    /// Can be overridden to include custom claims (roles, IDs, permissions, etc).
    /// </summary>
    protected virtual IEnumerable<Claim> GetClaimsForLogin(AccountModel account, LoginRequest request)
    {
        string principal = "";

        if (request is UsernamePasswordLoginRequest usernamePasswordLoginRequest)
        {
            principal = usernamePasswordLoginRequest.Username;
        }
        else if (request is EmailPasswordLoginRequest emailPasswordLoginRequest)
        {
            principal = emailPasswordLoginRequest.Email;
        }
        else
        {
            throw new NotSupportedException($"Unsupported login request type {request.GetType().Name}.");
        }

        string groupKey = SessionGroupKeyStrategy(account.GenId);
        var ip = HttpContext.Connection.RemoteIpAddress?.ToString();
        return [new Claim(ClaimTypes.Name, principal), new Claim("GroupKey", groupKey ?? ""), new Claim(JwtRegisteredClaimNames.Sub, account.GenId), new Claim("Ip", ip ?? "")];
    }
}