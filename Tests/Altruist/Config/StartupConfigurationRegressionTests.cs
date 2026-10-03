/*
Copyright 2025 Aron Gere
Licensed under the Apache License, Version 2.0
*/

using Altruist;
using Altruist.Security;
using Altruist.Transport;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

using Moq;

namespace Tests.Altruist.Config;

// ------------------------------------------------------------------ fixtures discovered by the real startup

public sealed class StartupTestCounters
{
    public int AStarts;
    public int BStarts;
}

public sealed class FirstHostedService : IHostedService
{
    private readonly StartupTestCounters _counters;
    public FirstHostedService(StartupTestCounters counters) => _counters = counters;
    public Task StartAsync(CancellationToken cancellationToken) { Interlocked.Increment(ref _counters.AStarts); return Task.CompletedTask; }
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

public sealed class SecondHostedService : IHostedService
{
    private readonly StartupTestCounters _counters;
    public SecondHostedService(StartupTestCounters counters) => _counters = counters;
    public Task StartAsync(CancellationToken cancellationToken) { Interlocked.Increment(ref _counters.BStarts); return Task.CompletedTask; }
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

/// <summary>Gated on a key that is never configured: must not be routed.</summary>
[ApiController]
[Route("altruist-tests/gated")]
[ConditionalOnConfig("altruist:tests:startup:gated-controller-enabled")]
public sealed class GatedTestController : ControllerBase
{
    [HttpGet] public string Get() => "gated";
}

[ApiController]
[Route("altruist-tests/open")]
public sealed class OpenTestController : ControllerBase
{
    [HttpGet] public string Get() => "open";
}

/// <summary>Shield handler that is not registered in DI (only constructible).</summary>
public sealed class DenyEverythingShield : IShieldAuth
{
    public Task<AuthResult> HandleAuthAsync(IAuthContext context) =>
        Task.FromResult(new AuthResult(Microsoft.AspNetCore.Authorization.AuthorizationResult.Failed(), null));
}

[ApiController]
[Route("altruist-tests/shielded")]
[Shield(typeof(DenyEverythingShield))]
public sealed class ShieldedTestController : ControllerBase
{
    [HttpGet] public string Get() => "secret";
}

[ApiController]
[Route("altruist-tests/boom")]
public sealed class ThrowingTestController : ControllerBase
{
    public const string Secret = "internal-detail-7f3a";
    [HttpGet] public string Get() => throw new InvalidOperationException(Secret);
}

/// <summary>Portal fixtures for the WebSocket route registration test (plain classes, no gates).</summary>
[Portal("/altruist-tests/ws-open")]
public sealed class OpenTestPortal { }

[Portal("/altruist-tests/ws-disabled")]
[ConditionalOnConfig("altruist:tests:startup:disabled-portal-enabled")]
public sealed class DisabledTestPortal { }

/// <summary>Records which portal routes a (fake) websocket transport was asked to map.</summary>
public sealed class RecordingWebSocketTransport : ITransport
{
    public List<(Type Type, string Path)> Mapped { get; } = new();
    public string TransportType => "websocket";
    public void RouteTraffic(IApplicationBuilder app) { }
    public void UseTransportEndpoints<TType>(IApplicationBuilder app, string path) where TType : class => Mapped.Add((typeof(TType), path));
    public void UseTransportEndpoints(IApplicationBuilder app, Type type, string path) => Mapped.Add((type, path));
}

/// <summary>
/// Boots the real AltruistStartupConfiguration.BuildAndStartAsync on an ephemeral Kestrel port.
/// Regressions: several singletons of one service type (e.g. IHostedService) were promoted as N
/// copies of the last one; [ConditionalOnConfig] did not gate controllers or WebSocket portal
/// routes; a Shield handler not registered by its concrete type let requests through; the
/// developer exception page (stack traces) was enabled in every environment.
/// </summary>
public sealed class StartupConfigurationRegressionTests : IAsyncLifetime
{
    private readonly StartupTestCounters _counters = new();
    private readonly RecordingWebSocketTransport _transport = new();
    private WebApplication _app = null!;
    private HttpClient _http = null!;
    private ServiceProvider? _bootstrap;

    public async Task InitializeAsync()
    {
        var root = new ServiceCollection();
        root.AddSingleton(_counters);
        root.AddSingleton(new MutableConfigSource(new MutableConfigProvider()));
        root.AddSingleton<IHostedService, FirstHostedService>();
        root.AddSingleton<IHostedService, SecondHostedService>();
        var status = new Mock<IServerStatus>();
        status.Setup(s => s.Status).Returns(ReadyState.Alive);
        root.AddSingleton(status.Object);

        var context = new Mock<IAltruistContext>();
        context.SetupProperty(c => c.ServerInfo);
        context.Setup(c => c.Endpoints).Returns(new HashSet<string>());

        var bootstrap = _bootstrap = root.BuildServiceProvider();
        var startup = new AltruistStartupConfiguration(
            new ApplicationArgs(), "127.0.0.1", "0", "/", "/ws",
            NullLoggerFactory.Instance, context.Object, status.Object, new ITransport[] { _transport });

        _app = (await startup.BuildAndStartAsync(root, bootstrap))!;
        var address = _app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        _http = new HttpClient { BaseAddress = new Uri(address) };
    }

    public async Task DisposeAsync()
    {
        _http.Dispose();
        await _app.StopAsync();
        await _app.DisposeAsync();
        if (_bootstrap is not null)
            await _bootstrap.DisposeAsync();
    }

    [Fact]
    public void Every_hosted_service_is_started_exactly_once()
    {
        // Before the fix: the second implementation was registered twice (started twice) and the
        // first one was dropped.
        Assert.Equal(1, _counters.AStarts);
        Assert.Equal(1, _counters.BStarts);
        var hosted = _app.Services.GetServices<IHostedService>().ToList();
        Assert.Single(hosted.OfType<FirstHostedService>());
        Assert.Single(hosted.OfType<SecondHostedService>());
    }

    [Fact]
    public async Task Controllers_gated_by_ConditionalOnConfig_are_not_routed()
    {
        Assert.Equal("open", await _http.GetStringAsync("/altruist-tests/open"));
        var gated = await _http.GetAsync("/altruist-tests/gated");
        Assert.Equal(System.Net.HttpStatusCode.NotFound, gated.StatusCode);
    }

    [Fact]
    public async Task Shield_with_an_unregistered_handler_still_denies()
    {
        var response = await _http.GetAsync("/altruist-tests/shielded");
        Assert.Equal(System.Net.HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Unhandled_errors_do_not_leak_details_outside_Development()
    {
        var response = await _http.GetAsync("/altruist-tests/boom");
        Assert.Equal(System.Net.HttpStatusCode.InternalServerError, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        if (_app.Environment.IsDevelopment())
            Assert.Contains(ThrowingTestController.Secret, body);
        else
            Assert.DoesNotContain(ThrowingTestController.Secret, body);
    }

    [Fact]
    public void Disabled_portals_get_no_websocket_route()
    {
        Assert.Contains(_transport.Mapped, m => m.Type == typeof(OpenTestPortal) && m.Path == "/altruist-tests/ws-open");
        Assert.DoesNotContain(_transport.Mapped, m => m.Type == typeof(DisabledTestPortal));
    }
}
