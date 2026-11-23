/*
Copyright 2025 Aron Gere
Licensed under the Apache License, Version 2.0
*/

using Altruist;
using Altruist.Security;

using Microsoft.Extensions.DependencyInjection;

namespace Tests.Framework.DI;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class StandardErrorCaptureCollection
{
    public const string Name = "standard-error-capture";
}

/// <summary>
/// The eager [PostConstruct] pass resolved every descriptor from the root provider, so every server start wrote
/// "Failed to resolve" lines for open generic templates (IOptions&lt;&gt;, ILogger&lt;&gt;) and for the ASP.NET
/// authorization policy cache, which needs the web host's endpoint data source. Console.Error is process-global, so
/// these tests run outside the parallel collections.
/// </summary>
[Collection(StandardErrorCaptureCollection.Name)]
public sealed class PostConstructResolutionTests
{
    private static string RunPostConstructsCapturingErrors(IServiceCollection services)
    {
        using var provider = services.BuildServiceProvider();
        var previous = Console.Error;
        using var captured = new StringWriter();
        Console.SetError(captured);
        try
        {
            AltruistDI.RunPostConstructsAsync(provider, services).GetAwaiter().GetResult();
        }
        finally
        {
            Console.SetError(previous);
        }
        return captured.ToString();
    }

    [Fact]
    public void Open_generic_registrations_are_not_resolved()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddOptions();

        Assert.DoesNotContain("Failed to resolve", RunPostConstructsCapturingErrors(services));
    }

    [Fact]
    public void Jwt_security_registrations_resolve_from_the_root_provider()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        new AuthConfiguration(new string('k', 48), "jwt", 32, mapInboundClaims: null, nameClaim: null)
            .Configure(services).GetAwaiter().GetResult();

        Assert.DoesNotContain("Failed to resolve", RunPostConstructsCapturingErrors(services));
    }
}
