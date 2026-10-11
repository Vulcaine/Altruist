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
    /// <summary>Writes the response for a failed authentication/authorization. The default challenge is suppressed when a writer exists.</summary>
    /// <param name="context">The current request.</param>
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
/// <para>
/// Tokens are validated with issuer and audience <c>Altruist</c>, zero clock skew, and must not be expired. The
/// <see cref="SymmetricSecurityKey"/> and <see cref="TokenValidationParameters"/> are registered as singletons so token
/// issuers can sign with the same key. <c>mode: session</c> registers nothing here (session auth is handled elsewhere);
/// any other non-empty mode logs a warning and configures no authentication.
/// </para>
/// </summary>
[ServiceConfiguration]
public sealed class AuthConfiguration : IAltruistConfiguration
{
    /// <summary>The key used when <c>altruist:security:key</c> is not set. Public (it ships with Altruist): never valid in Production.</summary>
    public const string DevelopmentKey = "VGhpcy1pcy1hLWRldmVsb3BtZW50LXNlY3JldC1rZXktMTIzNDU2";
    /// <summary>Default minimum key length in bytes (UTF-8) for <c>altruist:security:min-key-bytes</c>.</summary>
    public const int DefaultMinKeyBytes = 32;

    /// <inheritdoc/>
    public bool IsConfigured { get; set; }

    /// <summary>HS256 signing secret (<c>altruist:security:key</c>, UTF-8 bytes used as the key).</summary>
    public string SecretKey { get; set; } = "";

    /// <summary>Authentication mode (<c>altruist:security:mode</c>): <c>jwt</c> (default) or <c>session</c>.</summary>
    public string Mode { get; set; } = "";

    /// <summary>Minimum key length in bytes enforced by the key guard (<c>altruist:security:min-key-bytes</c>).</summary>
    public int MinKeyBytes { get; set; } = DefaultMinKeyBytes;

    /// <summary><c>altruist:security:jwt:map-inbound-claims</c>; null keeps the ASP.NET default (true).</summary>
    public bool? MapInboundClaims { get; set; }

    /// <summary><c>altruist:security:jwt:name-claim</c>, trimmed; null keeps the default name claim type.</summary>
    public string? NameClaim { get; set; }

    /// <summary>Created by the framework from configuration; see the type summary for the keys.</summary>
    /// <param name="secretKey"><c>altruist:security:key</c> (defaults to <see cref="DevelopmentKey"/>).</param>
    /// <param name="mode"><c>altruist:security:mode</c> (default <c>jwt</c>).</param>
    /// <param name="minKeyBytes"><c>altruist:security:min-key-bytes</c> (default 32).</param>
    /// <param name="mapInboundClaims"><c>altruist:security:jwt:map-inbound-claims</c>.</param>
    /// <param name="nameClaim"><c>altruist:security:jwt:name-claim</c>.</param>
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

    /// <summary>
    /// In <c>jwt</c> mode: enforces the key policy (may throw in Production), then registers the signing key, validation
    /// parameters, JWT bearer authentication (with <see cref="IAuthChallengeWriter"/> hooks) and authorization.
    /// </summary>
    /// <param name="services">Collection to configure.</param>
    /// <exception cref="InvalidOperationException">Production, <c>altruist:security</c> configured, and the key is unfit.</exception>
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
    /// <param name="parameters">Parameters to harden in place.</param>
    public static void Harden(TokenValidationParameters parameters)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        parameters.ValidAlgorithms = new[] { SecurityAlgorithms.HmacSha256 };
        parameters.RequireSignedTokens = true;
        parameters.RequireExpirationTime = true;
    }

    /// <summary>Why <paramref name="key"/> is unfit for production, or null when it is fine.</summary>
    /// <param name="key">Candidate secret.</param>
    /// <param name="minKeyBytes">Minimum UTF-8 byte length.</param>
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
    /// <param name="key">Configured secret.</param>
    /// <param name="minKeyBytes">Minimum UTF-8 byte length.</param>
    /// <param name="securityConfigured">Whether the application has an <c>altruist:security</c> section.</param>
    /// <param name="production">Whether the environment is Production.</param>
    /// <param name="logger">Logger for the warning.</param>
    /// <exception cref="InvalidOperationException"><paramref name="production"/> and <paramref name="securityConfigured"/> and the key is unfit.</exception>
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
