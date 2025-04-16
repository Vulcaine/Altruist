using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Altruist.Security;
using Altruist.Database;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;

namespace Altruist.Security;

public abstract class AuthController : ControllerBase
{
    private readonly ILoginService _loginService;
    private readonly IIssuer _issuer;
    private readonly TokenSessionSyncService? _syncService;

    protected AuthController(VaultRepositoryFactory factory, IIssuer issuer,
        IServiceProvider serviceProvider)
    {
        _loginService = LoginService(factory);
        _issuer = issuer;
        _syncService = serviceProvider.GetService<TokenSessionSyncService>();
    }

    [HttpPost("login")]
    public async Task<IActionResult> Login(
    [FromBody][ModelBinder(BinderType = typeof(LoginRequestBinder))] LoginRequest request)
    {
        if (request is not UsernamePasswordLoginRequest usernamePasswordLoginRequest)
            return Unauthorized();

        var account = await _loginService.Login(usernamePasswordLoginRequest);
        if (account == null)
            return Unauthorized();

        var claims = new List<Claim> { new(ClaimTypes.Name, usernamePasswordLoginRequest.Username) };
        var issue = IssueToken(claims);

        if (!await CreateAndSaveAuthSessionAsync(issue, account.GenId))
        {
            return Unauthorized("Only clients with IP address are allowed to connect.");
        }

        return OkOrUnauthorized(issue);
    }

    [HttpPost("refresh")]
    public async Task<IActionResult> Refresh()
    {
        if (_syncService == null)
            throw new InvalidOperationException("TokenSessionSyncService is not registered. Did you forget to call .StatefulToken()?");

        var authHeader = HttpContext.Request.Headers["Authorization"].ToString();
        if (string.IsNullOrWhiteSpace(authHeader) || !authHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            return Unauthorized("Invalid token format. Expected: Bearer <access_token>;<access_protocol>;<refresh_token>;<refresh_protocol>");

        var parts = authHeader["Bearer ".Length..].Trim().Split(';');
        if (parts.Length != 4)
            return Unauthorized("Invalid token format. Expected: <access_token>;<access_protocol>;<refresh_token>;<refresh_protocol>");

        var accessToken = parts[0];
        var accessProtocol = parts[1].ToLowerInvariant();
        var refreshToken = parts[2];
        var refreshProtocol = parts[3].ToLowerInvariant();

        if (!(accessProtocol == "jwt" || accessProtocol == "session") || !(refreshProtocol == "jwt" || refreshProtocol == "session"))
            return Unauthorized("Invalid token protocol. Supported protocols: jwt, session");

        // Lookup by access token + protocol
        var accessKey = $"{accessToken};{accessProtocol}";
        var cached = await _syncService.FindCachedByIdAsync(accessKey);
        if (cached?.IsAccessTokenValid() != true || cached?.IsRefreshTokenValid() != true)
            return Unauthorized("Invalid session.");

        // Ensure refresh token and protocol match the cached session data
        var expectedRefreshKey = $"{cached.RefreshToken};{refreshProtocol}";
        var providedRefreshKey = $"{refreshToken};{refreshProtocol}";

        if (!string.Equals(providedRefreshKey, expectedRefreshKey, StringComparison.Ordinal))
            return Unauthorized("Refresh token does not match the one associated with this session.");

        ClaimsPrincipal? principal = null;

        if (accessProtocol == "jwt")
        {
            if (_issuer is not JwtTokenIssuer jwtIssuer)
                return Unauthorized();

            principal = GetPrincipalFromToken(accessToken, jwtIssuer.JwtOptions.TokenValidationParameters);
            if (principal == null)
                return Unauthorized();
        }
        else
        {
            // session protocol: already trusted
            var identity = new ClaimsIdentity([
                new Claim(ClaimTypes.NameIdentifier, cached.PrincipalId)
            ], "Session");

            principal = new ClaimsPrincipal(identity);
        }

        var issue = IssueToken(principal.Claims);
        if (!await CreateAndSaveAuthSessionAsync(issue, cached.PrincipalId))
        {
            return Unauthorized("Only clients with IP address are allowed to connect.");
        }

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

    protected async Task InvalidateAllSessions(string principalId)
    {
        if (_syncService != null)
        {
            var cursor = await _syncService.FindAllCachedAsync(principalId);
            foreach (var session in cursor)
            {
                await _syncService.DeleteAsync(session.GenId, principalId);
            }
        }
    }

    protected async Task InvalidateExpiredSessions(AccountVault account)
    {
        if (_syncService != null)
        {
            var cursor = await _syncService.FindAllCachedAsync(account.GenId);
            foreach (var session in cursor)
            {
                if (!session.IsAccessTokenValid() && !session.IsRefreshTokenValid())
                {
                    await _syncService.DeleteAsync(session.GenId, account.GenId);
                }
            }
        }
    }

    private async Task<bool> CreateAndSaveAuthSessionAsync(TokenIssue? issue, string principalId)
    {
        if (issue != null && _syncService != null)
        {
            var ip = HttpContext.Connection.RemoteIpAddress?.ToString();

            if (ip == null)
            {
                return false;
            }

            var authData = new AuthTokenSessionVault
            {
                AccessToken = issue.AccessToken,
                AccessExpiration = issue.AccessExpiration,
                RefreshExpiration = issue.RefreshExpiration,
                RefreshToken = issue.RefreshToken,
                PrincipalId = principalId,
                Ip = ip,
                GenId = issue.AccessToken
            };

            await SaveAuthSessionAsync(authData, principalId);
        }

        return true;
    }

    protected async Task SaveAuthSessionAsync(AuthTokenSessionVault session, string principal)
    {
        if (_syncService != null)
        {
            await InvalidateAllSessions(principal);
            await _syncService.SaveAsync(session, principal);
        }
    }

    public abstract ILoginService LoginService(VaultRepositoryFactory factory);
}
