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

using System.Text;
using Altruist.Persistence;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Tokens;

namespace Altruist.Security;

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
    public static WebApplicationBuilder StatefulToken<TKeyspace>(this WebApplicationBuilder builder)
        where TKeyspace : class, IKeyspace, new()
    {
        builder.Services.AddSingleton(sp =>
        {
            var repoFactory = sp.GetRequiredService<VaultRepositoryFactory>();
            var repo = repoFactory.Make<TKeyspace>();
            return new TokenSessionSyncService(sp.GetRequiredService<ICacheProvider>(), repo.Select<AuthTokenSessionModel>());
        });
        builder.Services.AddSingleton<ISyncService>(sp => sp.GetRequiredService<TokenSessionSyncService>());
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
        builder.Services.AddSingleton<SessionTokenAuth>();
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
        // Dummy, fixed key for development use only.
        // You can generate one with any 256-bit base64-encoded string (32 bytes).
        const string devSecretKey =
        // This-is-a-development-secret-key. Provided to make it work out of the box without the need for a complex configuration."
        "VGhpcy1pcy1hLWRldmVsb3BtZW50LXNlY3JldC1rZXktMTIzNDU2";

        var keyBytes = Convert.FromBase64String(devSecretKey);
        var signingKey = new SymmetricSecurityKey(keyBytes);

        builder.Services.AddSingleton(signingKey);

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
        var validationParameters = new TokenValidationParameters
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

        builder.Services.AddSingleton(sp => validationParameters);
        builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
        .AddJwtBearer(options =>
        {
            options.TokenValidationParameters = validationParameters;
            configureOptions?.Invoke(options);
        });


        builder.Services.AddAuthorization(options => authorizationOptions?.Invoke(options));

        builder.Services.AddSingleton<JwtTokenValidator>();
        builder.Services.AddSingleton<JwtTokenIssuer>();
        builder.Services.AddKeyedSingleton<IIssuer>(IssuerKeys.JwtToken, (sp, key) => sp.GetRequiredService<JwtTokenIssuer>());
        builder.Services.AddSingleton<JwtAuth>();
        builder.Services.AddSingleton<IPasswordHasher, BcryptPasswordHasher>();


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
