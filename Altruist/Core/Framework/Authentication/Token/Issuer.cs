using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

public interface IIssuer<T>
{
    T Issue();
}


public class SessionToken
{
    public string Token { get; set; } = "";
    public DateTime Expiration { get; set; }
}

public class JwtToken
{
    public string AccessToken { get; set; } = "";
    public string RefreshToken { get; set; } = "";
    public string Algorithm { get; set; } = "";
}

public class SessionTokenIssuer : IIssuer<SessionToken>
{
    public SessionToken Issue()
    {
        return new SessionToken
        {
            Token = Guid.NewGuid().ToString(),
            Expiration = DateTime.UtcNow.AddHours(1)
        };
    }
}

public class JwtTokenIssuer : IIssuer<JwtToken>
{
    private readonly JwtBearerOptions _jwtOptions;
    private IEnumerable<Claim>? _customClaims;
    private bool _useJwtRefreshToken = false;
    private TimeSpan _refreshTokenExpiry = TimeSpan.FromMinutes(30);

    public JwtTokenIssuer(IOptionsMonitor<JwtBearerOptions> jwtOptions)
    {
        _jwtOptions = jwtOptions.Get(JwtBearerDefaults.AuthenticationScheme);
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
        var signingKey = _jwtOptions.TokenValidationParameters.IssuerSigningKey
            as SymmetricSecurityKey ?? throw new InvalidOperationException("Signing key is not configured.");

        var creds = new SigningCredentials(signingKey, SecurityAlgorithms.HmacSha256);

        var token = new JwtSecurityToken(
            issuer: _jwtOptions.TokenValidationParameters.ValidIssuer,
            audience: _jwtOptions.TokenValidationParameters.ValidAudience,
            claims: claims,
            expires: expires,
            signingCredentials: creds
        );

        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    public JwtToken Issue()
    {
        var signingKey = _jwtOptions.TokenValidationParameters.IssuerSigningKey
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
            refreshToken = GenerateJwtToken(claims, DateTime.UtcNow + _refreshTokenExpiry);
        }
        else
        {
            refreshToken = GenerateRandomRefreshToken();
        }

        return new JwtToken
        {
            AccessToken = accessToken,
            RefreshToken = refreshToken,
            Algorithm = creds.Algorithm
        };
    }
}



public static class Issuer
{
    public static IIssuer<SessionToken> Session = new SessionTokenIssuer();
    public static IIssuer<JwtToken> Jwt(IOptionsMonitor<JwtBearerOptions> jwtOptions) => new JwtTokenIssuer(jwtOptions);
}
