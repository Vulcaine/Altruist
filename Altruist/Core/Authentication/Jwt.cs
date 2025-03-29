using System.IdentityModel.Tokens.Jwt;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.IdentityModel.Tokens;

namespace Altruist.Authentication;

public class JWTShield : IShield
{
    private readonly IConfiguration _config;

    public JWTShield(IConfiguration config)
    {
        _config = config;
    }

    public virtual Task<bool> AuthenticateAsync(HttpContext context)
    {
        if (!context.Request.Headers.TryGetValue("Authorization", out var tokenHeader))
            return Task.FromResult(false);

        var token = tokenHeader.ToString().Replace("Bearer ", "");
        var tokenHandler = new JwtSecurityTokenHandler();
        var key = Encoding.ASCII.GetBytes(_config["JwtSecretKey"]!);

        try
        {
            tokenHandler.ValidateToken(token, new TokenValidationParameters
            {
                ValidateIssuerSigningKey = true,
                IssuerSigningKey = new SymmetricSecurityKey(key),
                ValidateIssuer = false,
                ValidateAudience = false
            }, out _);

            return Task.FromResult(true);
        }
        catch
        {
            return Task.FromResult(false);
        }
    }
}
