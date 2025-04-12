using System.Text;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Tokens;

namespace Altruist.Authentication;

public static class WebAppAuthExtensions
{
    public static WebApplicationBuilder AddJwtAuth(
       this WebApplicationBuilder builder,
       Action<JwtBearerOptions>? configureOptions = null,
       Action<AuthorizationOptions>? authorizationOptions = null)
    {
        var sp = builder.Services.BuildServiceProvider();
        ILoggerFactory factory = sp.GetRequiredService<ILoggerFactory>();
        ILogger logger = factory.CreateLogger<object>();
        builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer(options =>
            {
                options.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidIssuer = "Altruist",
                    ValidAudience = "Altruist",
                    IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes("altruist-secret-key")),
                    ValidateIssuer = true,
                    ValidateAudience = true,
                    ValidateIssuerSigningKey = true,
                    ValidateLifetime = true,
                    ClockSkew = TimeSpan.Zero
                };
                configureOptions?.Invoke(options);
            });

        builder.Services.AddAuthorization(options =>
        {
            authorizationOptions?.Invoke(options);
        });

        builder.Services.AddScoped<ITokenValidator, JwtTokenValidator>();
        builder.Services.AddScoped<IShieldAuth, JwtAuth>();

        logger.LogInformation("🔐 JWT authentication activated. Your app is armored and ready to secure connections?! Well, almost..");

        if (configureOptions == null)
        {
            logger.LogWarning("🚨 I’m being a bit of a nitpicker here, but it seems like the JWT bearer options are missing. For now, I’m using a default secret key, which is good for prototyping, but it's not exactly the most secure for the long haul. Don't forget it! 🦸‍♂️");
        }

        return builder;
    }

    public static WebApplicationBuilder AddJwtAuth(
    this WebApplicationBuilder builder,
    string secretKey,
    Action<AuthorizationOptions>? authorizationOptions = null)
    {
        var sp = builder.Services.BuildServiceProvider();
        ILoggerFactory factory = sp.GetRequiredService<ILoggerFactory>();
        ILogger logger = factory.CreateLogger<object>();
        builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer(options =>
            {
                options.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidIssuer = "Altruist",
                    ValidAudience = "Altruist",
                    IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(secretKey)),
                    ValidateIssuer = true,
                    ValidateAudience = true,
                    ValidateIssuerSigningKey = true,
                    ValidateLifetime = true,
                    ClockSkew = TimeSpan.Zero
                };
            });

        builder.Services.AddAuthorization(options =>
        {
            authorizationOptions?.Invoke(options);
        });

        builder.Services.AddScoped<ITokenValidator, JwtTokenValidator>();
        builder.Services.AddScoped<IShieldAuth, JwtAuth>();

        logger.LogInformation("🔐 JWT authentication activated. Your app is armored and ready to secure connections!");
        return builder;
    }


    public static IServiceCollection AddSessionTokenAuth(this IServiceCollection services)
    {
        services.AddScoped<SessionTokenAuth>();
        return services;
    }
}