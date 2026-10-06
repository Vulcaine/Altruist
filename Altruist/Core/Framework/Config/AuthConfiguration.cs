using System.Text;

using Altruist.Contracts;

using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Tokens;

namespace Altruist.Security;

/// <summary>
/// Writes the response body of a failed JWT bearer authentication (401) or authorization (403),
/// e.g. a JSON error your clients understand. Register one as a service to use it; without one
/// ASP.NET's default empty responses (with a <c>WWW-Authenticate</c> header for 401) are sent.
/// </summary>
public interface IAuthChallengeWriter
{
    /// <param name="statusCode">401 (no or invalid token) or 403 (valid token, not allowed).</param>
    Task WriteAsync(HttpContext context, int statusCode);
}

/// <summary>
/// JWT bearer authentication (<c>altruist:security:mode: jwt</c>, the default) with the HS256 key
/// <c>altruist:security:key</c>. Tokens must be HS256-signed and carry an expiry.
/// <para>
/// Key guard: in Production (<see cref="AltruistEnvironment"/>) an application with an
/// <c>altruist:security</c> section refuses to start when the key is the built-in development
/// key, unset, or shorter than <c>altruist:security:min-key-bytes</c> (default 32); other
/// environments log a warning. Options under <c>altruist:security:jwt</c>:
/// <c>map-inbound-claims</c> (ASP.NET maps <c>sub</c> to <c>NameIdentifier</c> and so on; default
/// true) and <c>name-claim</c> (the claim <c>User.Identity.Name</c> reads).
/// </para>
/// </summary>
[ServiceConfiguration]
public sealed class AuthConfiguration : IAltruistConfiguration
{
    /// <summary>The key used when <c>altruist:security:key</c> is not set. Public (it ships with Altruist): never valid in Production.</summary>
    public const string DevelopmentKey = "VGhpcy1pcy1hLWRldmVsb3BtZW50LXNlY3JldC1rZXktMTIzNDU2";
    public const int DefaultMinKeyBytes = 32;

    public bool IsConfigured { get; set; }
    public string SecretKey { get; set; } = "";
    public string Mode { get; set; } = "";
    public int MinKeyBytes { get; set; } = DefaultMinKeyBytes;
    public bool? MapInboundClaims { get; set; }
    public string? NameClaim { get; set; }

    public AuthConfiguration(
        [AppConfigValue("altruist:security:key", DevelopmentKey)]
        string secretKey,
        [AppConfigValue("altruist:security:mode", "jwt")]
        string mode,
        [AppConfigValue("altruist:security:min-key-bytes", "32")]
        int minKeyBytes = DefaultMinKeyBytes,
        [AppConfigValue("altruist:security:jwt:map-inbound-claims")]
        bool? mapInboundClaims = null,
        [AppConfigValue("altruist:security:jwt:name-claim")]
        string? nameClaim = null)
    {
        SecretKey = secretKey;
        Mode = mode;
        MinKeyBytes = minKeyBytes;
        MapInboundClaims = mapInboundClaims;
        NameClaim = string.IsNullOrWhiteSpace(nameClaim) ? null : nameClaim.Trim();
    }

    public Task Configure(IServiceCollection services)
    {
        var sp = services.BuildServiceProvider();
        var logger = sp.GetRequiredService<ILoggerFactory>().CreateLogger<AuthConfiguration>();

        if (string.Equals(Mode, "jwt", StringComparison.OrdinalIgnoreCase))
        {
            EnforceKeyPolicy(SecretKey, MinKeyBytes, AppConfigLoader.Load().GetSection("altruist:security").Exists(),
                AltruistEnvironment.IsProduction, logger);

            var signingKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(SecretKey));
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
            Harden(validationParameters);
            if (NameClaim is not null)
                validationParameters.NameClaimType = NameClaim;

            services.AddSingleton(signingKey);
            services.AddSingleton(validationParameters);

            services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
                .AddJwtBearer(o =>
                {
                    o.TokenValidationParameters = validationParameters;
                    if (MapInboundClaims is { } map)
                        o.MapInboundClaims = map;
                    o.Events = new JwtBearerEvents
                    {
                        OnChallenge = async ctx =>
                        {
                            var writer = ctx.HttpContext.RequestServices.GetService<IAuthChallengeWriter>();
                            if (writer is null)
                                return;
                            ctx.HandleResponse();
                            await writer.WriteAsync(ctx.HttpContext, StatusCodes.Status401Unauthorized);
                        },
                        OnForbidden = ctx =>
                        {
                            var writer = ctx.HttpContext.RequestServices.GetService<IAuthChallengeWriter>();
                            return writer is null ? Task.CompletedTask : writer.WriteAsync(ctx.HttpContext, StatusCodes.Status403Forbidden);
                        },
                    };
                });

            services.AddAuthorization((AuthorizationOptions _) => { });
            logger.LogInformation("🔐 JWT authentication activated. Your app is armored and ready to secure connections!");
        }
        else if (string.Equals(Mode, "session", StringComparison.OrdinalIgnoreCase))
        {
            logger.LogInformation("🔐 Session authentication activated. Your app is armored and ready to secure connections!");
        }
        else if (!string.IsNullOrWhiteSpace(Mode))
        {
            logger.LogWarning("🚨 Authentication mode '{Mode}' not supported; no authentication configured.", Mode);
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Pins HS256 (a token signed with another algorithm, or unsigned, is rejected) and requires an
    /// expiry. Applied to the bearer validation parameters.
    /// </summary>
    public static void Harden(TokenValidationParameters parameters)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        parameters.ValidAlgorithms = new[] { SecurityAlgorithms.HmacSha256 };
        parameters.RequireSignedTokens = true;
        parameters.RequireExpirationTime = true;
    }

    /// <summary>Why <paramref name="key"/> is unfit for production, or null when it is fine.</summary>
    public static string? KeyProblem(string? key, int minKeyBytes = DefaultMinKeyBytes)
    {
        if (string.IsNullOrWhiteSpace(key))
            return "altruist:security:key (JWT secret) is not configured.";
        if (key == DevelopmentKey)
            return "altruist:security:key is the Altruist development default.";
        if (Encoding.UTF8.GetByteCount(key) < minKeyBytes)
            return $"altruist:security:key is shorter than {minKeyBytes} bytes.";
        return null;
    }

    /// <summary>
    /// Throws (refuses to start) in Production when the application configures security and the
    /// key has a <see cref="KeyProblem"/>; logs a warning otherwise.
    /// </summary>
    public static void EnforceKeyPolicy(string? key, int minKeyBytes, bool securityConfigured, bool production, ILogger logger)
    {
        var problem = KeyProblem(key, minKeyBytes);
        if (problem is null)
            return;
        if (production && securityConfigured)
            throw new InvalidOperationException(
                $"Refusing to start: {problem} Set ALTRUIST__SECURITY__KEY to a random secret of at least {minKeyBytes} bytes.");
        logger.LogWarning("🚨 {Problem} Tokens signed with it can be forged; {Where}.", problem,
            production ? "configure altruist:security before relying on authentication" : "this is refused in Production");
    }
}
