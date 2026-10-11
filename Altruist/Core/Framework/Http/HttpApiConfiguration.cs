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
/// <remarks>Registered as a singleton by <see cref="HttpApiConfiguration"/>; inject it to read the effective HTTP settings.</remarks>
/// <param name="Errors">Error format and messages (<c>altruist:server:http:errors</c>).</param>
/// <param name="Hardening">Hardening options (<c>altruist:server:http:hardening</c>).</param>
/// <param name="RateLimit">Rate-limit policies (<c>altruist:server:http:rate-limit</c>).</param>
public sealed record HttpApiSettings(HttpErrorOptions Errors, HttpHardeningOptions Hardening, HttpRateLimitOptions RateLimit)
{
    /// <summary>Reads all three sections from <paramref name="configuration"/>.</summary>
    /// <param name="configuration">Configuration root.</param>
    /// <returns>The settings.</returns>
    /// <exception cref="ArgumentException">A section holds an invalid value.</exception>
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
/// <remarks>Discovered and run automatically via <c>[ServiceConfiguration]</c>; reads config with <c>AppConfigLoader.Load()</c>.
/// Registers <see cref="HttpApiSettings"/>, <see cref="HttpErrorWriter"/>, and (per config) <see cref="HttpApiExceptionFilter"/>,
/// the invalid-model factory, <see cref="HttpErrorChallengeWriter"/>, <see cref="HttpRateLimitFilter"/> and Kestrel/forwarded-header options.</remarks>
[ServiceConfiguration]
public sealed class HttpApiConfiguration : IAltruistConfiguration
{
    /// <inheritdoc/>
    public bool IsConfigured { get; set; }

    /// <inheritdoc/>
    public Task Configure(IServiceCollection services)
    {
        Register(services, HttpApiSettings.FromConfiguration(AppConfigLoader.Load()));
        IsConfigured = true;
        return Task.CompletedTask;
    }

    /// <summary>Registers <paramref name="settings"/> (also for hosts you build yourself, e.g. tests).</summary>
    /// <param name="services">Service collection.</param>
    /// <param name="settings">Settings to register.</param>
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
