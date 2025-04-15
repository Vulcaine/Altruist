using System.Security.Cryptography;
using System.Text;
using Altruist.Contracts;
using Altruist.Database;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Tokens;

namespace Altruist.Authentication;

/// <summary>
/// Provides extension methods to configure authentication and token session handling for a WebApplicationBuilder.
/// </summary>
public static class WebAppAuthExtensions
{
    /// <summary>
    /// Registers stateful token persistence backed by both cache and database using a specific keyspace.
    /// </summary>
    /// <typeparam name="TKeyspace">The keyspace type used to resolve the vault for token storage.</typeparam>
    /// <param name="builder">The current WebApplicationBuilder instance.</param>
    /// <param name="token">The token identifying the database provider to use for persistence.</param>
    /// <returns>The modified WebApplicationBuilder.</returns>
    /// <remarks>
    /// This variant enables full persistence of token sessions using both database and cache.
    /// </remarks>
    public static WebApplicationBuilder StatefulToken<TKeyspace>(this WebApplicationBuilder builder, IDatabaseServiceToken token)
        where TKeyspace : class, IKeyspace, new()
    {
        builder.Services.AddSingleton(sp =>
        {
            var repoFactory = sp.GetRequiredService<VaultRepositoryFactory>();
            var repo = repoFactory.Make<TKeyspace>(token);
            return new TokenSessionSyncService(sp.GetRequiredService<ICacheProvider>(), repo.Select<AuthSessionVault>());
        });
        builder.Services.AddSingleton(typeof(IVaultCacheSyncService<>), sp => sp.GetRequiredService<TokenSessionSyncService>());
        builder.Services.AddSingleton<SessionTokenIssuer>();
        builder.Services.AddKeyedScoped<IIssuer>(IssuerKeys.SessionToken, (sp, key) => sp.GetRequiredService<SessionTokenIssuer>());
        return builder;
    }

    /// <summary>
    /// Registers token session synchronization using cache only (no database persistence).
    /// </summary>
    /// <param name="builder">The current WebApplicationBuilder instance.</param>
    /// <returns>The modified WebApplicationBuilder.</returns>
    /// <remarks>
    /// Use this method when token persistence is required only in-memory (cache), without backing database.
    /// </remarks>
    public static WebApplicationBuilder StatefulToken(this WebApplicationBuilder builder)
    {
        builder.Services.AddSingleton(sp =>
        {
            return new TokenSessionSyncService(sp.GetRequiredService<ICacheProvider>(), null);
        });
        builder.Services.AddSingleton<SessionTokenIssuer>();
        builder.Services.AddKeyedScoped<IIssuer>(IssuerKeys.SessionToken, (sp, key) => sp.GetRequiredService<SessionTokenIssuer>());
        return builder;
    }

    /// <summary>
    /// Adds JWT authentication to the application using a randomly generated secret key.
    /// </summary>
    /// <param name="builder">The current WebApplicationBuilder instance.</param>
    /// <param name="configureOptions">Optional action to further configure JWT bearer options.</param>
    /// <param name="authorizationOptions">Optional action to configure authorization policies.</param>
    /// <returns>The modified WebApplicationBuilder.</returns>
    /// <remarks>
    /// This is suitable for development and testing purposes. In production, use the overload that accepts a fixed secret key.
    /// </remarks>
    public static WebApplicationBuilder AddJwtAuth(
        this WebApplicationBuilder builder,
        Action<JwtBearerOptions>? configureOptions = null,
        Action<AuthorizationOptions>? authorizationOptions = null)
    {
        var secretKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        builder.Services.AddSingleton(new SymmetricSecurityKey(Convert.FromBase64String(secretKey)));

        return builder.ConfigureJwtAuth(configureOptions, authorizationOptions, isDefaultKey: true);
    }

    /// <summary>
    /// Adds JWT authentication to the application using a specified secret key.
    /// </summary>
    /// <param name="builder">The current WebApplicationBuilder instance.</param>
    /// <param name="secretKey">The symmetric key used to sign and validate JWT tokens.</param>
    /// <param name="authorizationOptions">Optional action to configure authorization policies.</param>
    /// <returns>The modified WebApplicationBuilder.</returns>
    public static WebApplicationBuilder AddJwtAuth(
        this WebApplicationBuilder builder,
        string secretKey,
        Action<AuthorizationOptions>? authorizationOptions = null)
    {
        builder.Services.AddSingleton(new SymmetricSecurityKey(Encoding.UTF8.GetBytes(secretKey)));
        return builder.ConfigureJwtAuth(null, authorizationOptions, isDefaultKey: false);
    }

    /// <summary>
    /// Internal helper method to configure JWT authentication and authorization services.
    /// </summary>
    /// <param name="builder">The current WebApplicationBuilder instance.</param>
    /// <param name="configureOptions">Optional action to configure JWT bearer options.</param>
    /// <param name="authorizationOptions">Optional action to configure authorization policies.</param>
    /// <param name="isDefaultKey">Indicates whether the signing key is a temporary, auto-generated key (for logging purposes).</param>
    /// <returns>The modified WebApplicationBuilder.</returns>
    private static WebApplicationBuilder ConfigureJwtAuth(
        this WebApplicationBuilder builder,
        Action<JwtBearerOptions>? configureOptions,
        Action<AuthorizationOptions>? authorizationOptions,
        bool isDefaultKey)
    {
        var sp = builder.Services.BuildServiceProvider();
        var logger = sp.GetRequiredService<ILoggerFactory>().CreateLogger<object>();
        var signingKey = sp.GetRequiredService<SymmetricSecurityKey>();

        builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer(options =>
            {
                options.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidIssuer = "Altruist",
                    ValidAudience = "Altruist",
                    IssuerSigningKey = signingKey,
                    ValidateIssuer = true,
                    ValidateAudience = true,
                    ValidateIssuerSigningKey = true,
                    ValidateLifetime = true,
                    ClockSkew = TimeSpan.Zero
                };

                configureOptions?.Invoke(options);
            });

        builder.Services.AddAuthorization(options => authorizationOptions?.Invoke(options));

        builder.Services.AddScoped<ITokenValidator, JwtTokenValidator>();
        builder.Services.AddScoped<JwtTokenIssuer>();
        builder.Services.AddKeyedScoped<IIssuer>(IssuerKeys.JwtToken, (sp, key) => sp.GetRequiredService<JwtTokenIssuer>());

        logger.LogInformation("🔐 JWT authentication activated. Your app is armored and ready to secure connections!");

        if (isDefaultKey)
        {
            logger.LogWarning("🚨 Default secret key in use. Great for development, but don't forget to replace it for production!");
        }

        return builder;
    }

    /// <summary>
    /// Adds session-based token authentication support by registering the <see cref="SessionTokenAuth"/> service.
    /// </summary>
    /// <param name="services">The application's service collection.</param>
    /// <returns>The updated IServiceCollection.</returns>
    public static WebApplicationBuilder AddSessionTokenAuth(this WebApplicationBuilder builder)
    {
        builder.Services.AddScoped<SessionTokenAuth>();
        return builder;
    }
}
