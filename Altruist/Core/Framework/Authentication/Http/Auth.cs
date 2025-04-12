using System.Security.Claims;
using Altruist.Database;
using Microsoft.AspNetCore.Mvc;

namespace Altruist.Auth;

public abstract class AuthController : ControllerBase
{
    private readonly ILoginService _loginService;
    private readonly IIssuer _issuer;

    public AuthController(
        VaultRepositoryFactory factory, IIssuer issuer)
    {
        _loginService = LoginService(factory);
        this._issuer = issuer;
    }

    [HttpPost("login")]
    public async Task<IActionResult> Login([FromBody][ModelBinder(BinderType = typeof(LoginRequestBinder))] LoginRequest request)
    {
        if (request is UsernamePasswordLoginRequest usernamePasswordLoginRequest)
        {
            var account = await _loginService.Login(usernamePasswordLoginRequest);
            if (account != null)
            {
                var claims = new List<Claim>
                {
                    new Claim(ClaimTypes.Name, usernamePasswordLoginRequest.Username)
                };

                var issuer = _issuer;

                if (issuer is JwtTokenIssuer jwtIssuer)
                {
                    issuer = jwtIssuer.WithClaims(claims);
                }

                var token = (TokenIssue)issuer
                    .Issue();

                return Ok(new AltruistLoginResponse
                {
                    AccessToken = token.AccessToken,
                    RefreshToken = token.RefreshToken
                });
            }

            return Unauthorized();
        }

        return Unauthorized();
    }

    public abstract ILoginService LoginService(VaultRepositoryFactory factory);
}
