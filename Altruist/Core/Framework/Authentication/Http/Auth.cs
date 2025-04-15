using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography.X509Certificates;
using Altruist.Authentication;
using Altruist.Database;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;

namespace Altruist.Auth;

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

        if (issue != null && _syncService != null)
        {
            var ip = HttpContext.Connection.RemoteIpAddress?.ToString();

            if (ip == null)
            {
                return Unauthorized("Only clients with IP address are allowed to connect.");
            }

            var authData = new AuthSessionVault
            {
                AccessToken = issue.AccessToken,
                Expiration = issue.Expiration,
                RefreshToken = issue.RefreshToken,
                PrincipalId = account.GenId,
                Ip = ip,
                GenId = account.GenId
            };

            await _syncService.SaveAsync(authData);
        }

        return OkOrUnauthorized(issue);
    }

    [HttpPost("refresh")]
    public async Task<IActionResult> Refresh(
        [FromBody] string refreshToken)
    {
        if (_syncService == null)
            throw new InvalidOperationException("TokenSessionSyncService is not registered. Did you forget to call .StatefulToken()?");

        var cached = await _syncService.FindCachedByIdAsync(refreshToken);
        if (cached?.IsValid() != true)
            return Unauthorized();

        if (_issuer is not JwtTokenIssuer jwtIssuer)
            return Unauthorized();

        var principal = GetPrincipalFromToken(cached.AccessToken, jwtIssuer.JwtOptions.TokenValidationParameters);
        if (principal == null)
            return Unauthorized();

        var issue = IssueToken(principal.Claims);
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

    public abstract ILoginService LoginService(VaultRepositoryFactory factory);
}
