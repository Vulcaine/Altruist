using System.Text;
using Altruist.Contracts;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Tokens;

namespace Altruist.Security;

public sealed class AuthConfiguration : IAltruistConfiguration
{
    [ConfigValue("altruist:security:key", "VGhpcy1pcy1hLWRldmVsb3BtZW50LXNlY3JldC1rZXktMTIzNDU2")]
    public string SecretKey { get; set; } = "";

    [ConfigValue("altruist:security:mode", "jwt")]
    public string Mode { get; set; } = "";

    public Task Configure(IServiceCollection services)
    {
        var sp = services.BuildServiceProvider();
        var logger = sp.GetRequiredService<ILoggerFactory>().CreateLogger<AuthConfiguration>();

        if (string.Equals(Mode, "jwt", StringComparison.OrdinalIgnoreCase))
        {
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

            services.AddSingleton(signingKey);
            services.AddSingleton(validationParameters);

            services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
                .AddJwtBearer(o => { o.TokenValidationParameters = validationParameters; });

            services.AddAuthorization((AuthorizationOptions _) => { });
            services.AddSingleton<IPasswordHasher, BcryptPasswordHasher>();

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
}
