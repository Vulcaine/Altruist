using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Mvc;
using Microsoft.IdentityModel.Tokens;

namespace Altruist.Auth;

[ApiController]
[Route("api/auth")]
public abstract class AuthController : ControllerBase
{
    private readonly ILoginVault _loginVault;

    public AuthController(ILoginVault loginVault) => _loginVault = loginVault;

    [HttpPost("login")]
    public IActionResult Login([FromBody] LoginRequest request)
    {
        if (request is UsernamePasswordLoginRequest usernamePasswordLoginRequest) {
            if (_loginVault.Login(usernamePasswordLoginRequest)) {
                var tokenHandler = new JwtSecurityTokenHandler();
                var key = Encoding.UTF8.GetBytes("your_secret_key");
                var tokenDescriptor = new SecurityTokenDescriptor
                {
                    Subject = new ClaimsIdentity(new[] { new Claim(ClaimTypes.Name, usernamePasswordLoginRequest.Username) }),
                    Expires = DateTime.UtcNow.AddHours(1),
                    SigningCredentials = new SigningCredentials(new SymmetricSecurityKey(key), SecurityAlgorithms.HmacSha256Signature)
                };
                var token = tokenHandler.CreateToken(tokenDescriptor);
                return Ok(new { token = tokenHandler.WriteToken(token) });
            }

            return Unauthorized();
        }
        
        return Unauthorized();
    }
}