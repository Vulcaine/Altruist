using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace Altruist.Security;

public interface IIssuer
{
    IIssue Issue();
}


public static class IssuerKeys
{
    public const string SessionToken = "SessionToken";
    public const string JwtToken = "JwtToken";
}

public class SessionToken : TokenIssue
{
    public override string Type { get; set; } = "SessionToken";
}

public class JwtToken : TokenIssue
{
    public override string Type { get; set; } = "JwtToken";
}

public class SessionTokenIssuer : IIssuer
{
    public IIssue Issue()
    {
        return new SessionToken
        {
            AccessToken = Guid.NewGuid().ToString() + ";session",
            AccessExpiration = DateTime.UtcNow.AddHours(1)
        };
    }
}

public class JwtTokenIssuer : IIssuer
{
    public JwtBearerOptions JwtOptions { get; }
    private IEnumerable<Claim>? _customClaims;
    private bool _useJwtRefreshToken = false;
    private TimeSpan _refreshTokenExpiry = TimeSpan.FromMinutes(30);

    public JwtTokenIssuer(IOptionsMonitor<JwtBearerOptions> jwtOptions)
    {
        JwtOptions = jwtOptions.Get(JwtBearerDefaults.AuthenticationScheme);
    }

    public JwtTokenIssuer WithClaims(IEnumerable<Claim> claims)
    {
        _customClaims = claims;
        return this;
    }

    public JwtTokenIssuer SetRefreshTokenExpiry(TimeSpan expiration)
    {
        _refreshTokenExpiry = expiration;
        return this;
    }

    public JwtTokenIssuer UseJwtRefreshToken(TimeSpan? expiration)
    {
        SetRefreshTokenExpiry(expiration ?? TimeSpan.FromMinutes(30));
        _useJwtRefreshToken = true;
        return this;
    }

    private string GenerateRandomRefreshToken()
    {
        var bytes = new byte[32];
        using (var rng = RandomNumberGenerator.Create())
        {
            rng.GetBytes(bytes);
        }
        return Convert.ToBase64String(bytes);
    }

    private string GenerateJwtToken(IEnumerable<Claim> claims, DateTime expires)
    {
        var signingKey = JwtOptions.TokenValidationParameters.IssuerSigningKey
            as SymmetricSecurityKey ?? throw new InvalidOperationException("Signing key is not configured.");

        var creds = new SigningCredentials(signingKey, SecurityAlgorithms.HmacSha256);

        var token = new JwtSecurityToken(
            issuer: JwtOptions.TokenValidationParameters.ValidIssuer,
            audience: JwtOptions.TokenValidationParameters.ValidAudience,
            claims: claims,
            expires: expires,
            signingCredentials: creds
        );

        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    public IIssue Issue()
    {
        var signingKey = JwtOptions.TokenValidationParameters.IssuerSigningKey
            as SymmetricSecurityKey ?? throw new InvalidOperationException("Signing key is not configured.");

        var creds = new SigningCredentials(signingKey, SecurityAlgorithms.HmacSha256);

        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, Guid.NewGuid().ToString()),
            new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString())
        };

        if (_customClaims != null)
            claims.AddRange(_customClaims);

        var accessToken = GenerateJwtToken(claims, DateTime.UtcNow.AddHours(1));

        string refreshToken;
        if (_useJwtRefreshToken)
        {
            refreshToken = GenerateJwtToken(claims, DateTime.UtcNow + _refreshTokenExpiry) + ";jwt";
        }
        else
        {
            refreshToken = GenerateRandomRefreshToken() + ";session";
        }

        return new JwtToken
        {
            AccessToken = $"{accessToken};jwt",
            RefreshToken = $"{refreshToken}",
            Algorithm = creds.Algorithm
        };
    }
}



public static class Issuer
{
    public static IIssuer Session = new SessionTokenIssuer();
    public static IIssuer Jwt(IOptionsMonitor<JwtBearerOptions> jwtOptions) => new JwtTokenIssuer(jwtOptions);
}
