/*
Copyright 2025 Aron Gere
Licensed under the Apache License, Version 2.0
*/

using Altruist.Contracts;
using Altruist.Security;

using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Altruist.Http;

/// <summary>The HTTP API settings: <see cref="HttpErrorOptions"/>, <see cref="HttpHardeningOptions"/> and <see cref="HttpRateLimitOptions"/>.</summary>
public sealed record HttpApiSettings(HttpErrorOptions Errors, HttpHardeningOptions Hardening, HttpRateLimitOptions RateLimit)
{
    public static HttpApiSettings FromConfiguration(IConfiguration configuration) => new(
        HttpErrorOptions.FromConfiguration(configuration),
        HttpHardeningOptions.FromConfiguration(configuration),
        HttpRateLimitOptions.FromConfiguration(configuration));
}

/// <summary>
/// Registers the HTTP API features of <c>altruist:server:http</c> (<c>errors</c>, <c>hardening</c>,
/// <c>rate-limit</c>). Nothing changes for an app that configures none of them; the server adds the
/// middleware with <see cref="HttpHardeningExtensions.UseAltruistHttpHardening"/>.
/// </summary>
[ServiceConfiguration]
public sealed class HttpApiConfiguration : IAltruistConfiguration
{
    public bool IsConfigured { get; set; }

    public Task Configure(IServiceCollection services)
    {
        Register(services, HttpApiSettings.FromConfiguration(AppConfigLoader.Load()));
        IsConfigured = true;
        return Task.CompletedTask;
    }

    /// <summary>Registers <paramref name="settings"/> (also for hosts you build yourself, e.g. tests).</summary>
    public static void Register(IServiceCollection services, HttpApiSettings settings)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(settings);
        services.AddSingleton(settings);
        services.AddSingleton(new HttpErrorWriter(settings.Errors));

        if (settings.Errors.Format == HttpErrorFormat.Simple)
        {
            services.Configure<MvcOptions>(o => o.Filters.Add<HttpApiExceptionFilter>());
            // PostConfigure: MVC's own setup (AddControllers) sets the default factory.
            services.PostConfigure<ApiBehaviorOptions>(o => o.InvalidModelStateResponseFactory = ctx =>
                HttpApiExceptionFilter.InvalidModel(ctx, ctx.HttpContext.RequestServices.GetRequiredService<HttpErrorWriter>()));
            services.TryAddSingleton<IAuthChallengeWriter, HttpErrorChallengeWriter>();
        }

        if (settings.RateLimit.Policies.Count > 0)
            services.Configure<MvcOptions>(o => o.Filters.Add<HttpRateLimitFilter>());

        settings.Hardening.Register(services);
    }
}
