using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Altruist.Database;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
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

    protected AuthController(VaultRepositoryFactory factory, IIssuer issuer, IServiceProvider serviceProvider)
    {
        _loginService = LoginService(factory);
        _issuer = issuer;
        _syncService = serviceProvider.GetService<TokenSessionSyncService>();
    }

    protected async Task InvalidateAllSessions(string groupKey)
    {
        if (_syncService != null)
        {
            var cursor = await _syncService.FindAllCachedAsync(groupKey);
            foreach (var session in cursor)
            {
                await _syncService.DeleteAsync(session.GenId, groupKey);
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
                }
            }
        }
    }

    protected async Task<bool> CreateAndSaveAuthSessionAsync(TokenIssue? issue, string groupKey, string principal)
    {
        if (issue != null && _syncService != null)
        {
            var ip = HttpContext.Connection.RemoteIpAddress?.ToString();

            if (ip == null)
            {
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
                GenId = issue.AccessToken
            };

            await SaveAuthSessionAsync(authData, groupKey);
        }

        return true;
    }

    protected async Task SaveAuthSessionAsync(AuthTokenSessionModel session, string groupKey)
    {
        if (_syncService != null)
        {
            await InvalidateAllSessions(groupKey);
            await _syncService.SaveAsync(session, groupKey);
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
    public abstract ILoginService LoginService(VaultRepositoryFactory factory);
}

/// <summary>
/// Extends <see cref="AuthController"/> to implement JWT-specific login and refresh handling.
/// </summary>
public abstract class JwtAuthController : AuthController
{
    protected JwtAuthController(VaultRepositoryFactory factory, IIssuer issuer, IServiceProvider serviceProvider)
        : base(factory, issuer, serviceProvider) { }

    [HttpPost("signup")]
    public async Task<IActionResult> Signup([FromBody] SignupRequest request)
    {
        var account = await _loginService.Signup(request);

        if (account != null)
        {
            return Ok();
        }
        else
        {
            return BadRequest();
        }
    }

    [HttpPost("login/emailpwd")]
    public async Task<IActionResult> UsernamePasswordLogin(
        [FromBody] EmailPasswordLoginRequest request)
    {
        var account = await _loginService.Login(request);
        if (account == null)
            return Unauthorized();

        var claims = GetClaimsForLogin(account, request);
        var issue = IssueToken(claims);
        var groupKey = SessionGroupKeyStrategy(account.GenId);

        if (!await CreateAndSaveAuthSessionAsync(issue, groupKey, account.GenId))
            return Unauthorized("Only clients with IP address are allowed to connect.");

        return OkOrUnauthorized(issue);
    }

    [HttpPost("login/unamepwd")]
    public async Task<IActionResult> UsernamePasswordLogin(
        [FromBody] UsernamePasswordLoginRequest request)
    {
        var account = await _loginService.Login(request);
        if (account == null)
            return Unauthorized();

        var claims = GetClaimsForLogin(account, request);
        var issue = IssueToken(claims);
        var groupKey = SessionGroupKeyStrategy(account.GenId);

        if (!await CreateAndSaveAuthSessionAsync(issue, groupKey, account.GenId))
            return Unauthorized("Only clients with IP address are allowed to connect.");

        return OkOrUnauthorized(issue);
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

        var accessKey = $"{accessToken};jwt";
        var cached = await _syncService.FindCachedByIdAsync(accessKey);

        if (cached?.IsAccessTokenValid() != true || cached?.IsRefreshTokenValid() != true)
            return Unauthorized("Invalid or expired session.");

        var expectedRefreshKey = $"{cached.RefreshToken};jwt";
        var providedRefreshKey = $"{refreshToken};jwt";

        if (!string.Equals(providedRefreshKey, expectedRefreshKey, StringComparison.Ordinal))
            return Unauthorized("Refresh token mismatch.");

        if (_issuer is not JwtTokenIssuer jwtIssuer)
            return Unauthorized("JWT issuer not configured.");

        var principal = GetPrincipalFromToken(accessToken, jwtIssuer.JwtOptions.TokenValidationParameters);
        if (principal == null)
            return Unauthorized("Invalid access token.");

        var issue = IssueToken(principal.Claims);
        var groupKey = SessionGroupKeyStrategy(cached.PrincipalId);

        if (!await CreateAndSaveAuthSessionAsync(issue, groupKey, cached.PrincipalId))
            return Unauthorized("Couldn't identify client IP.");

        return OkOrUnauthorized(issue);
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

        return [new Claim(ClaimTypes.Name, principal)];
    }
}