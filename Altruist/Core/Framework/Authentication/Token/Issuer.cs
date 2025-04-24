/* 
Copyright 2025 Aron Gere

Licensed under the Apache License, Version 2.0 (the "License");
you may not use this file except in compliance with the License.
You may obtain a copy of the License at

    http://www.apache.org/licenses/LICENSE-2.0

Unless required by applicable law or agreed to in writing, software
distributed under the License is distributed on an "AS IS" BASIS,
WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
See the License for the specific language governing permissions and
limitations under the License.
*/

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
    private readonly TimeSpan _accessTokenExpiration;
    private readonly TimeSpan _refreshTokenExpiration;
    public SessionTokenIssuer(TimeSpan? accessTokenExpiration = null, TimeSpan? refreshTokenExpiration = null)
    {
        _accessTokenExpiration = accessTokenExpiration ?? TimeSpan.FromHours(1);
        _refreshTokenExpiration = refreshTokenExpiration ?? TimeSpan.FromDays(7);
    }

    public IIssue Issue()
    {
        return new SessionToken
        {
            AccessToken = Guid.NewGuid().ToString() + ";session",
            RefreshToken = Guid.NewGuid().ToString() + ";session",
            RefreshExpiration = DateTime.UtcNow + _refreshTokenExpiration,
            AccessExpiration = DateTime.UtcNow + _accessTokenExpiration
        };
    }
}

public class JwtTokenIssuer : IIssuer
{
    public JwtBearerOptions JwtOptions { get; }
    private IEnumerable<Claim>? _customClaims;
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
            new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString())
        };

        if (_customClaims != null)
            claims.AddRange(_customClaims);

        var accessToken = GenerateJwtToken(claims, DateTime.UtcNow.AddHours(1));

        string refreshToken;
        refreshToken = GenerateJwtToken(claims, DateTime.UtcNow + _refreshTokenExpiry) + ";jwt";

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
