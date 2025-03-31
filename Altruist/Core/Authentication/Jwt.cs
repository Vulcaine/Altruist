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

    public Task<AuthorizationResult> HandleAuthAsync(HttpContext context)
    {
        var token = context.Request.Headers["Authorization"].ToString().Replace("Bearer ", "");
        if (string.IsNullOrEmpty(token) || !_tokenValidator.ValidateToken(token))
        {
            return Task.FromResult(AuthorizationResult.Failed());
        }

        return Task.FromResult(AuthorizationResult.Success());
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
