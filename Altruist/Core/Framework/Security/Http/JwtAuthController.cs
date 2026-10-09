using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Security.Claims;

using Altruist.Security.Auth;

using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Tokens;

namespace Altruist.Security.Http;


/// <summary>
/// Extends <see cref="AuthController"/> to implement JWT-specific login, refresh handling,
/// and a session upgrade flow using <see cref="IAuthService"/> (HTTP version of AuthPortal).
/// </summary>
/// <remarks>
/// Derive a controller with your own <c>[Route]</c> to get <c>POST signup</c>, <c>login/emailpwd</c>,
/// <c>login/unamepwd</c>, <c>refresh</c> and <c>upgrade</c>. Credentials are checked by your
/// <see cref="ILoginService"/>; tokens come from <see cref="IJwtTokenIssuer"/> (access JWT 1 hour) and the session is
/// stored in <see cref="TokenSessionSyncService"/> (one session per group key). Override <see cref="GetClaimsForLogin"/>
/// to add claims and <see cref="AuthController.SessionGroupKeyStrategy"/> to group sessions differently.
/// <para>
/// For new applications a hand-written controller over <see cref="IAccessTokenIssuer"/>, <see cref="IRefreshTokenService"/>
/// and <see cref="RefreshCookie"/> gives configurable lifetimes and rotating, revocable refresh tokens.
/// </para>
/// </remarks>
/// <example>
/// <code>
/// [ApiController, Route("api/auth")]
/// public sealed class MyAuthController : JwtAuthController
/// {
///     public MyAuthController(IJwtTokenValidator v, ILoginService l, TokenSessionSyncService s,
///         IJwtTokenIssuer i, IAuthService a, ILoggerFactory f) : base(v, l, s, i, a, f) { }
/// }
/// </code>
/// </example>
public abstract class JwtAuthController : AuthController
{
    /// <summary>Validates access tokens presented to <see cref="Refresh"/>.</summary>
    protected readonly IJwtTokenValidator _tokenValidator;
    /// <summary>Performs the <see cref="Upgrade"/> flow.</summary>
    protected readonly IAuthService _authService;

    /// <summary>Creates the controller (derived controllers are resolved by MVC).</summary>
    /// <param name="jwtTokenValidator">Access-token validator.</param>
    /// <param name="loginService">Application login/signup logic.</param>
    /// <param name="tokenSessionSyncService">Session store.</param>
    /// <param name="issuer">JWT issuer.</param>
    /// <param name="authService">Upgrade service.</param>
    /// <param name="loggerFactory">Logger factory.</param>
    protected JwtAuthController(
        IJwtTokenValidator jwtTokenValidator,
        ILoginService loginService,
        TokenSessionSyncService tokenSessionSyncService,
        IJwtTokenIssuer issuer,
        IAuthService authService,
        ILoggerFactory loggerFactory)
        : base(issuer, loginService, tokenSessionSyncService, loggerFactory)
    {
        _tokenValidator = jwtTokenValidator;
        _authService = authService;
    }

    // --------------------------------------------------------------------
    // UPGRADE HOOKS (HTTP equivalent of AuthPortal hooks)
    // --------------------------------------------------------------------

    /// <summary>
    /// Hook called before upgrade, allows modifying the context.
    /// Default: returns the context unchanged.
    /// </summary>
    protected virtual UpgradeAuthRequest OnUpgrade(UpgradeAuthRequest context, string clientId)
        => context;

    /// <summary>
    /// Hook called after a successful upgrade (token issued).
    /// Default: no-op.
    /// </summary>
    protected virtual Task OnUpgradeSuccess(UpgradeAuthRequest context, string clientId, IIssue issue)
        => Task.CompletedTask;

    /// <summary>
    /// Hook called after a failed upgrade attempt.
    /// Default: no-op.
    /// </summary>
    protected virtual Task OnUpgradeFailed(UpgradeAuthRequest context, string clientId)
        => Task.CompletedTask;

    /// <summary>
    /// Accepts an <see cref="UpgradeAuthRequest"/> in the body, calls <see cref="IAuthService.Upgrade"/>,
    /// and returns the issued token/packet as JSON. On failure, returns 401/500. Retries up to 3 times when the
    /// session store reports a concurrency conflict.
    /// </summary>
    [HttpPost("upgrade")]
    public async Task<IActionResult> Upgrade([FromBody] UpgradeAuthRequest context)
    {
        var clientId = context.ClientId ?? HttpContext.Connection.Id ?? Guid.NewGuid().ToString("N");

        const int maxRetries = 3;
        for (int attempt = 1; attempt <= maxRetries; attempt++)
        {
            try
            {
                context = OnUpgrade(context, clientId);

                var issue = await _authService.Upgrade(context);

                if (issue is null)
                {
                    await OnUpgradeFailed(context, clientId);
                    return Unauthorized(new { code = HttpStatusCode.Unauthorized, reason = "Invalid or expired token" });
                }

                await OnUpgradeSuccess(context, clientId, issue);
                return Ok(issue);
            }
            catch (SecurityTokenExpiredException)
            {
                await OnUpgradeFailed(context, clientId);
                return Unauthorized(new { code = StatusCodes.Status401Unauthorized, reason = "Invalid or expired token" });
            }
            catch (Exception ex) when (ex.Message.Contains("concurrency", StringComparison.OrdinalIgnoreCase) && attempt < maxRetries)
            {
                await Task.Delay(10 * attempt);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[upgrade] Authentication upgrade failed: {ExType}: {ExMessage}", ex.GetType().Name, ex.Message);
                await OnUpgradeFailed(context, clientId);
                return StatusCode(StatusCodes.Status500InternalServerError, new { code = TransportCode.InternalServerError, reason = "Authentication upgrade failed." });
            }
        }

        return StatusCode(StatusCodes.Status500InternalServerError, new { reason = "Upgrade failed after retries." });
    }

    /// <summary>
    /// <c>POST signup</c>: requires email, username and password, then calls <see cref="ILoginService.SignupAsync"/>.
    /// 200 (empty) when an account was created, 400 with the error otherwise, 500 on exceptions. Issues no tokens.
    /// </summary>
    [HttpPost("signup")]
    public async Task<IActionResult> Signup([FromBody] SignupRequest request)
    {
        if (request.Email == null || string.IsNullOrWhiteSpace(request.Email))
        {
            _logger.LogWarning("[signup] ❌ Signup failed – missing email");
            return BadRequest("Email is required.");
        }

        if (request.Username == null || string.IsNullOrWhiteSpace(request.Username))
        {
            _logger.LogWarning("[signup] ❌ Signup failed – missing username");
            return BadRequest("Username is required.");
        }

        if (request.Password == null || string.IsNullOrWhiteSpace(request.Password))
        {
            _logger.LogWarning("[signup] ❌ Signup failed – missing password");
            return BadRequest("Password is required.");
        }

        try
        {
            var signupResult = await _loginService.SignupAsync(request);
            var account = signupResult.Model;

            if (account != null)
            {
                _logger.LogInformation($"[signup][{request.Email}] ✅ Signup succeeded – user ID: {account.StorageId}");
                return Ok();
            }
            else
            {
                _logger.LogWarning($"[signup][{request.Email}] ❌ Signup failed – {signupResult.Error}");
                return BadRequest(signupResult.Error);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"[signup][{request.Email}] ❌ Signup exception");
            return StatusCode(StatusCodes.Status500InternalServerError, "Signup failed. Please try again.");
        }
    }

    /// <summary>
    /// <c>POST login/emailpwd</c>: checks the credentials with <see cref="ILoginService.LoginAsync"/>, issues a JWT pair with
    /// <see cref="GetClaimsForLogin"/> and stores the session (replacing the group's other sessions). Returns an
    /// <see cref="AltruistLoginResponse"/>, or 401.
    /// </summary>
    [HttpPost("login/emailpwd")]
    public async Task<IActionResult> EmailPasswordLogin([FromBody] EmailPasswordLoginRequest request)
    {
        try
        {
            var loginResult = await _loginService.LoginAsync(request);
            var account = loginResult.Model;
            if (account == null)
            {
                _logger.LogWarning($"[login-email][{request.Email}] ❌ Login failed");
                return Unauthorized();
            }

            var claims = GetClaimsForLogin(account, request);
            var issue = IssueToken(claims);
            var groupKey = SessionGroupKeyStrategy(account.StorageId);

            if (!await CreateAndSaveAuthSessionAsync(issue, groupKey, account.StorageId, request.Fingerprint))
                return Unauthorized($"[login-email][${request.Email}] Only clients with IP address are allowed to connect.");

            _logger.LogInformation($"[login-email][{groupKey}] ✅ Login succeeded (email: {request.Email})");
            return OkOrUnauthorized(issue);
        }
        catch
        {
            return Unauthorized();
        }
    }

    /// <summary>
    /// <c>POST login/unamepwd</c>: like <see cref="EmailPasswordLogin"/> for user name and password, but currently stores no
    /// session, so <see cref="Refresh"/> cannot be used with these tokens.
    /// </summary>
    [HttpPost("login/unamepwd")]
    public async Task<IActionResult> UsernamePasswordLogin([FromBody] UsernamePasswordLoginRequest request)
    {
        try
        {
            var loginResult = await _loginService.LoginAsync(
                new UsernamePasswordLoginRequest(request.Username, request.Password));
            var account = loginResult.Model;
            if (account == null)
            {
                _logger.LogWarning($"[login-uname][{request.Username}] ❌ Login failed");
                return Unauthorized();
            }

            var claims = GetClaimsForLogin(account, request);
            var issue = IssueToken(claims);
            var groupKey = SessionGroupKeyStrategy(account.StorageId);

            // if (!await CreateAndSaveAuthSessionAsync(issue, groupKey, account.StorageId, request.Fingerprint))
            //     return Unauthorized($"[login-uname][${request.Username}] Only clients with IP address are allowed to connect.");

            _logger.LogInformation($"[login-uname][{groupKey}] ✅ Login succeeded (username: {request.Username})");
            return OkOrUnauthorized(issue);
        }
        catch
        {
            return Unauthorized();
        }
    }

    /// <summary>
    /// <c>POST refresh</c> with <c>Authorization: Bearer &lt;access&gt;;jwt;&lt;refresh&gt;;jwt</c>: checks the stored session
    /// (refresh token, expiry, fingerprint) and issues a new pair carrying the old claims. The access token must still be
    /// valid (it is validated with lifetime checks), so refresh before it expires. Returns an
    /// <see cref="AltruistLoginResponse"/>, or 401.
    /// </summary>
    /// <exception cref="InvalidOperationException">When no <see cref="TokenSessionSyncService"/> is registered.</exception>
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
            var claims = await _tokenValidator.ValidateToken(accessToken);
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
                // Never log the refresh token itself: it is a bearer secret.
                _logger.LogWarning("[refresh] ❌ Invalid or expired session for the presented refresh token");
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

    // --------------------------------------------------------------------
    // Helpers
    // --------------------------------------------------------------------

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

        string groupKey = SessionGroupKeyStrategy(account.StorageId);
        var ip = HttpContext.Connection.RemoteIpAddress?.ToString();

        var claims = new List<Claim>
            {
                new Claim(ClaimTypes.Name, principal),
                new Claim("GroupKey", groupKey ?? ""),
                new Claim(JwtRegisteredClaimNames.Sub, account.StorageId),
                new Claim("Ip", ip ?? "")
            };

        if (!string.IsNullOrEmpty(request.Fingerprint))
        {
            claims.Add(new Claim("Fingerprint", request.Fingerprint));
        }

        return claims;
    }
}
