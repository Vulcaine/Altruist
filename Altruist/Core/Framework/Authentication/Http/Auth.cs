using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;

namespace Altruist.Auth;

public abstract class AuthController : ControllerBase
{
    private readonly ILoginService<LoginRequest> _loginService;
    private readonly JwtTokenIssuer _jwtIssuer;

    public AuthController(ILoginService<LoginRequest> loginService, JwtTokenIssuer jwtIssuer)
    {
        _loginService = loginService;
        _jwtIssuer = jwtIssuer;
    }

    [HttpPost("login")]
    public async Task<IActionResult> Login([FromBody] LoginRequest request)
    {
        if (request is UsernamePasswordLoginRequest usernamePasswordLoginRequest)
        {
            var success = await _loginService.Login(usernamePasswordLoginRequest);
            if (success)
            {
                var claims = new List<Claim>
                {
                    new Claim(ClaimTypes.Name, usernamePasswordLoginRequest.Username)
                };

                var token = _jwtIssuer
                    .WithClaims(claims)
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
}
