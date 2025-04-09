using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;

namespace Altruist.Authentication;

public static class WebAppAuthExtensions
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

    public static IServiceCollection AddSessionTokenAuth(this IServiceCollection services)
    {
        services.AddScoped<SessionTokenAuth>();
        return services;
    }
}