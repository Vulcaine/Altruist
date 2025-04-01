using System.IdentityModel.Tokens.Jwt;
using System.Text;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;

namespace Altruist.Authentication;

public class JwtAuth : IShieldAuth
{
    private readonly ITokenValidator _tokenValidator;

    public JwtAuth(ITokenValidator tokenValidator)
    {
        _tokenValidator = tokenValidator;
    }

    private readonly JwtSecurityTokenHandler _tokenHandler = new();

    public Task<AuthResult> HandleAuthAsync(IAuthContext context)
    {
        var token = GetTokenFromRequest(context);
        if (string.IsNullOrEmpty(token) || !_tokenValidator.ValidateToken(token))
        {
            return Task.FromResult(new AuthResult(AuthorizationResult.Failed(), null!));
        }

        var authDetails = ExtractAuthDetails(token);
        return Task.FromResult(new AuthResult(AuthorizationResult.Success(), authDetails));
    }

    private string GetTokenFromRequest(IAuthContext context)
    {
        if (context is HttpAuthContext httpAuthContext)
        {
            var request = httpAuthContext.HttpContext.Request;
            return request.Headers["Authorization"].ToString().Replace("Bearer ", "");
        }
        else
        {
            throw new NotSupportedException($"Unsupported authentication context type {context.GetType().Name}.");
        }
    }

    private AuthDetails ExtractAuthDetails(string token)
    {
        var jwt = _tokenHandler.ReadJwtToken(token);
        var expClaim = jwt.Claims.FirstOrDefault(c => c.Type == JwtRegisteredClaimNames.Exp);

        if (expClaim == null || !long.TryParse(expClaim.Value, out long expUnix))
        {
            throw new Exception("Invalid JWT: Missing or malformed expiration claim.");
        }

        var expirationTime = DateTimeOffset.FromUnixTimeSeconds(expUnix);
        var remainingTime = expirationTime - DateTimeOffset.UtcNow;

        return new AuthDetails(token, remainingTime);
    }
}


public class JwtTokenValidator : ITokenValidator
{
    private readonly JwtSecurityTokenHandler _tokenHandler;
    private readonly TokenValidationParameters _validationParams;

    public JwtTokenValidator(IConfiguration configuration)
    {
        _tokenHandler = new JwtSecurityTokenHandler();

        _validationParams = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            ValidIssuer = configuration["Jwt:Issuer"],
            ValidAudience = configuration["Jwt:Audience"],
            IssuerSigningKey = new SymmetricSecurityKey(
                Encoding.UTF8.GetBytes(configuration["Jwt:SecretKey"]!)
            )
        };
    }

    public bool ValidateToken(string token)
    {
        try
        {
            _tokenHandler.ValidateToken(token, _validationParams, out _);
            return true;
        }
        catch
        {
            return false;
        }
    }
}

public static class WebAppExtensions
{
    public static WebApplicationBuilder AddJwtAuth(
        this WebApplicationBuilder builder,
        Action<JwtBearerOptions>? configureOptions = null,
        Action<AuthorizationOptions>? authorizationOptions = null)
    {

        builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer(options =>
            {
                configureOptions?.Invoke(options);
            });

        builder.Services.AddAuthorization(options =>
        {
            authorizationOptions?.Invoke(options);
        });

        builder.Services.AddScoped<ITokenValidator, JwtTokenValidator>();
        builder.Services.AddScoped<IShieldAuth, JwtAuth>();

        return builder;
    }
}

[AttributeUsage(AttributeTargets.Method | AttributeTargets.Class, Inherited = true, AllowMultiple = true)]
public class JwtShieldAttribute : ShieldAttribute
{
    public JwtShieldAttribute() : base(typeof(JwtAuth)) { }
}
