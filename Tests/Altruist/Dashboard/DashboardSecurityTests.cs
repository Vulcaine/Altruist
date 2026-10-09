/*
Copyright 2025 Aron Gere
Licensed under the Apache License, Version 2.0
*/

using System.Net;
using System.Security.Claims;
using System.Text;
using System.Text.Json;

using Altruist;
using Altruist.Dashboard;
using Altruist.InMemory;
using Altruist.Transport;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

using Moq;

namespace Tests.Altruist.Dashboard;

/// <summary>Cache value type for the cache edit tests.</summary>
public sealed class DashboardCacheProbe
{
    public string Name { get; set; } = "";
}

[ConditionalOnAssembly("No.Such.Assembly.For.Altruist.Tests")]
public sealed class NeedsMissingAssembly { }

[ConditionalOnAssembly("Altruist.Dashboard")]
public sealed class NeedsDashboardAssembly { }

/// <summary>Unit tests of <see cref="DashboardAccessMiddleware"/>, redaction and <see cref="ConditionalOnAssemblyAttribute"/>.</summary>
public sealed class DashboardAccessMiddlewareTests
{
    private const string Token = "0123456789abcdef-secret";

    private sealed class Env : IHostEnvironment
    {
        public Env(string name) => EnvironmentName = name;
        public string EnvironmentName { get; set; }
        public string ApplicationName { get; set; } = "tests";
        public string ContentRootPath { get; set; } = "/";
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    private static async Task<(int Status, bool Reached, HttpContext Ctx)> Run(
        Dictionary<string, string?> config, string environment, Action<HttpContext> setup, IServiceProvider? services = null)
    {
        var cfg = new ConfigurationBuilder().AddInMemoryCollection(config).Build();
        var reached = false;
        var mw = new DashboardAccessMiddleware(_ => { reached = true; return Task.CompletedTask; }, cfg, new Env(environment), NullLoggerFactory.Instance);
        var ctx = new DefaultHttpContext { RequestServices = services ?? new ServiceCollection().BuildServiceProvider() };
        ctx.Response.Body = new MemoryStream();
        ctx.Request.Path = "/dashboard/v1/summary";
        ctx.Connection.RemoteIpAddress = IPAddress.Parse("203.0.113.9");
        setup(ctx);
        await mw.InvokeAsync(ctx);
        return (reached ? 200 : ctx.Response.StatusCode, reached, ctx);
    }

    private static Dictionary<string, string?> Enabled(string? token = null, string? policy = null) => new()
    {
        ["altruist:dashboard:enabled"] = "true",
        ["altruist:dashboard:token"] = token,
        ["altruist:dashboard:policy"] = policy,
    };

    [Fact]
    public async Task Non_dashboard_paths_pass_untouched()
    {
        var (status, reached, _) = await Run(Enabled(), "Production", c => c.Request.Path = "/api/things");
        Assert.True(reached);
        Assert.Equal(200, status);
    }

    [Fact]
    public async Task Without_credentials_outside_Development_the_dashboard_is_closed()
    {
        var (status, reached, _) = await Run(Enabled(), "Production", _ => { });
        Assert.False(reached);
        Assert.Equal(503, status);

        // Even from loopback.
        (status, reached, _) = await Run(Enabled(), "Production", c => c.Connection.RemoteIpAddress = IPAddress.Loopback);
        Assert.Equal(503, status);
    }

    [Fact]
    public async Task A_too_short_token_counts_as_not_configured()
    {
        var (status, _, _) = await Run(Enabled(token: "short"), "Production", c => c.Request.Headers[DashboardAccessOptions.TokenHeader] = "short");
        Assert.Equal(503, status);
    }

    [Fact]
    public async Task Development_without_credentials_allows_direct_loopback_only()
    {
        var (status, reached, _) = await Run(Enabled(), "Development", c => c.Connection.RemoteIpAddress = IPAddress.Loopback);
        Assert.True(reached);

        (status, reached, _) = await Run(Enabled(), "Development", c => c.Connection.RemoteIpAddress = IPAddress.IPv6Loopback);
        Assert.True(reached);

        (status, reached, _) = await Run(Enabled(), "Development", _ => { });
        Assert.False(reached);
        Assert.Equal(403, status);

        // A local reverse proxy must not make remote clients look local.
        (status, reached, _) = await Run(Enabled(), "Development", c =>
        {
            c.Connection.RemoteIpAddress = IPAddress.Loopback;
            c.Request.Headers["X-Forwarded-For"] = "198.51.100.7";
        });
        Assert.Equal(403, status);

        var off = Enabled();
        off["altruist:dashboard:allow-loopback-without-token"] = "false";
        (status, _, _) = await Run(off, "Development", c => c.Connection.RemoteIpAddress = IPAddress.Loopback);
        Assert.Equal(503, status);
    }

    [Fact]
    public async Task Token_is_required_and_checked()
    {
        var (status, reached, ctx) = await Run(Enabled(Token), "Production", _ => { });
        Assert.False(reached);
        Assert.Equal(401, status);
        Assert.Contains("Bearer", ctx.Response.Headers.WWWAuthenticate.ToString());

        (status, reached, _) = await Run(Enabled(Token), "Production", c => c.Request.Headers[DashboardAccessOptions.TokenHeader] = Token + "x");
        Assert.False(reached);
        Assert.Equal(403, status);

        (status, reached, _) = await Run(Enabled(Token), "Production", c => c.Request.Headers[DashboardAccessOptions.TokenHeader] = Token);
        Assert.True(reached);

        (status, reached, _) = await Run(Enabled(Token), "Production", c => c.Request.Headers.Authorization = "Bearer " + Token);
        Assert.True(reached);

        // A token also applies to loopback in Development (no anonymous bypass once credentials exist).
        (status, reached, _) = await Run(Enabled(Token), "Development", c => c.Connection.RemoteIpAddress = IPAddress.Loopback);
        Assert.Equal(401, status);
    }

    [Fact]
    public async Task Browser_login_exchanges_the_token_for_a_session_cookie_and_strips_it_from_the_url()
    {
        var (status, reached, ctx) = await Run(Enabled(Token), "Production", c =>
        {
            c.Request.Method = "GET";
            c.Request.Path = "/altruist/dashboard/sessions";
            c.Request.QueryString = new QueryString($"?tab=2&{DashboardAccessOptions.LoginQueryParameter}={Token}");
        });
        Assert.False(reached);
        Assert.Equal(302, status);
        Assert.Equal("/altruist/dashboard/sessions?tab=2", ctx.Response.Headers.Location.ToString());
        var setCookie = ctx.Response.Headers.SetCookie.ToString();
        Assert.Contains(DashboardAccessOptions.CookieName + "=" + DashboardAccess.SessionCookieValue(Token), setCookie);
        Assert.Contains("httponly", setCookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=strict", setCookie, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(Token, setCookie);

        (status, reached, _) = await Run(Enabled(Token), "Production", c =>
            c.Request.Headers.Cookie = $"{DashboardAccessOptions.CookieName}={DashboardAccess.SessionCookieValue(Token)}");
        Assert.True(reached);

        (status, reached, _) = await Run(Enabled(Token), "Production", c =>
            c.Request.Headers.Cookie = $"{DashboardAccessOptions.CookieName}={DashboardAccess.SessionCookieValue("another-token-0123456")}");
        Assert.Equal(403, status);
    }

    [Fact]
    public async Task A_configured_policy_grants_access_to_users_that_satisfy_it()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAuthorization(o => o.AddPolicy("ops", p => p.RequireClaim("role", "ops")));
        var sp = services.BuildServiceProvider();

        var (status, reached, _) = await Run(Enabled(policy: "ops"), "Production", _ => { }, sp);
        Assert.Equal(401, status);

        (status, reached, _) = await Run(Enabled(policy: "ops"), "Production",
            c => c.User = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim("role", "player") }, "test")), sp);
        Assert.Equal(403, status);

        (status, reached, _) = await Run(Enabled(policy: "ops"), "Production",
            c => c.User = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim("role", "ops") }, "test")), sp);
        Assert.True(reached);
    }

    [Theory]
    [InlineData("altruist:persistence:database:password", true)]
    [InlineData("altruist:persistence:database:connection-string", true)]
    [InlineData("altruist:security:jwt:signing-key", true)]
    [InlineData("altruist:security:api-key", true)]
    [InlineData("altruist:dashboard:token", true)]
    [InlineData("altruist:oauth:client-secret", true)]
    [InlineData("altruist:persistence:database:keyspace", false)]
    [InlineData("altruist:server:http:port", false)]
    public void Secret_keys_are_recognised(string key, bool secret) =>
        Assert.Equal(secret, DashboardAccess.IsSecretKey(key));

    [Fact]
    public void Connection_string_values_are_redacted_whatever_their_key()
    {
        Assert.Equal("***", DashboardAccess.RedactValue("altruist:db:url", "Host=db;Username=u;Password=p"));
        Assert.Equal("***", DashboardAccess.RedactValue("altruist:cache:url", "redis://user:pw@cache:6379"));
        Assert.Equal("8080", DashboardAccess.RedactValue("altruist:server:http:port", "8080"));
        Assert.Equal("", DashboardAccess.RedactValue("altruist:db:password", ""));
    }

    [Fact]
    public void ConditionalOnAssembly_is_evaluated()
    {
        var cfg = new ConfigurationBuilder().Build();
        Assert.False(DependencyResolver.ShouldRegister(typeof(NeedsMissingAssembly), cfg, NullLogger.Instance));
        Assert.True(DependencyResolver.ShouldRegister(typeof(NeedsDashboardAssembly), cfg, NullLogger.Instance));
        Assert.Equal("Altruist.Dashboard", typeof(SessionDashboardController).Assembly.GetName().Name);
    }
}

/// <summary>A live host with the dashboard controllers, checking mapping, protection, redaction and cache edits end to end.</summary>
public sealed class DashboardHostTests
{
    private const string Token = "host-test-token-0123456789";

    private sealed class Host : IAsyncDisposable
    {
        public WebApplication App = null!;
        public HttpClient Http = null!;
        public ServiceProvider Bootstrap = null!;
        public InMemoryCache Cache = null!;

        public async ValueTask DisposeAsync()
        {
            Http.Dispose();
            await App.StopAsync();
            await App.DisposeAsync();
            await Bootstrap.DisposeAsync();
        }
    }

    private static async Task<Host> Start(Dictionary<string, string?> config)
    {
        // Make sure the dashboard assembly is loaded so MVC discovers its controllers.
        _ = typeof(SessionDashboardController).Assembly;

        // Like a real app: the Altruist configuration is the IConfiguration of the bootstrap services.
        IConfiguration appConfig = new ConfigurationBuilder().AddInMemoryCollection(config).Build();

        var cache = new InMemoryCache();
        var root = new ServiceCollection();
        root.AddSingleton(appConfig);
        root.AddSingleton(new MutableConfigSource(new MutableConfigProvider()));
        root.AddSingleton(new JsonSerializerOptions(JsonSerializerDefaults.Web));
        root.AddSingleton<ICacheProvider>(cache);
        root.AddSingleton<IMemoryCacheProvider>(cache);
        root.AddSingleton(new Mock<IConnectionManager>().Object);
        root.AddSingleton(new Mock<ISocketManager>().Object);
        var status = new Mock<IServerStatus>();
        status.Setup(s => s.Status).Returns(ReadyState.Alive);
        root.AddSingleton(status.Object);
        var context = new Mock<IAltruistContext>();
        context.SetupProperty(c => c.ServerInfo);
        context.Setup(c => c.Endpoints).Returns(new HashSet<string>());

        var bootstrap = root.BuildServiceProvider();
        var startup = new AltruistStartupConfiguration(
            new ApplicationArgs(), "127.0.0.1", "0", "/", "/ws",
            NullLoggerFactory.Instance, context.Object, status.Object, Array.Empty<ITransport>());
        var app = (await startup.BuildAndStartAsync(root, bootstrap))!;
        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        return new Host { App = app, Bootstrap = bootstrap, Cache = cache, Http = new HttpClient { BaseAddress = new Uri(address) } };
    }

    private static HttpRequestMessage Req(HttpMethod method, string path, string? token = Token, HttpContent? content = null)
    {
        var req = new HttpRequestMessage(method, path) { Content = content };
        if (token is not null)
            req.Headers.Add(DashboardAccessOptions.TokenHeader, token);
        return req;
    }

    [Fact]
    public async Task Disabled_dashboard_maps_no_controllers_not_even_sessions()
    {
        await using var host = await Start(new() { ["altruist:dashboard:token"] = Token });
        foreach (var path in new[] { "/dashboard/v1/sessions", "/dashboard/v1/summary", "/dashboard/v1/cache/info", "/dashboard/v1/lab/actions" })
        {
            using var res = await host.Http.SendAsync(Req(HttpMethod.Get, path));
            Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
        }
    }

    [Fact]
    public async Task Enabled_dashboard_requires_the_token()
    {
        await using var host = await Start(new()
        {
            ["altruist:dashboard:enabled"] = "true",
            ["altruist:dashboard:token"] = Token,
            ["altruist:persistence:database:password"] = "hunter2-db-password",
            ["altruist:persistence:database:host"] = "db.internal",
        });

        foreach (var path in new[] { "/dashboard/v1/sessions", "/dashboard/v1/summary", "/dashboard/v1/cache/info" })
        {
            using var anonymous = await host.Http.SendAsync(Req(HttpMethod.Get, path, token: null));
            Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);

            using var wrong = await host.Http.SendAsync(Req(HttpMethod.Get, path, token: Token + "-nope"));
            Assert.Equal(HttpStatusCode.Forbidden, wrong.StatusCode);
        }

        using var ok = await host.Http.SendAsync(Req(HttpMethod.Get, "/dashboard/v1/cache/info"));
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);

        using var summary = await host.Http.SendAsync(Req(HttpMethod.Get, "/dashboard/v1/summary"));
        Assert.Equal(HttpStatusCode.OK, summary.StatusCode);
        var body = await summary.Content.ReadAsStringAsync();
        Assert.DoesNotContain("hunter2-db-password", body);
        Assert.DoesNotContain(Token, body);
        Assert.Contains("db.internal", body);
        Assert.Contains("***", body);
    }

    [Fact]
    public async Task Enabled_dashboard_without_credentials_outside_Development_answers_503()
    {
        await using var host = await Start(new() { ["altruist:dashboard:enabled"] = "true" });
        if (host.App.Environment.IsDevelopment())
            return; // the test runner was started with ASPNETCORE_ENVIRONMENT=Development
        using var res = await host.Http.SendAsync(Req(HttpMethod.Get, "/dashboard/v1/cache/info", token: null));
        Assert.Equal(HttpStatusCode.ServiceUnavailable, res.StatusCode);
    }

    [Fact]
    public async Task Cache_edits_reject_type_names_that_are_not_cached_or_vault_types()
    {
        await using var host = await Start(new()
        {
            ["altruist:dashboard:enabled"] = "true",
            ["altruist:dashboard:token"] = Token,
        });

        static StringContent Json(object o) => new(JsonSerializer.Serialize(o), Encoding.UTF8, "application/json");

        // Arbitrary framework type: never loaded from client input.
        using var arbitrary = await host.Http.SendAsync(Req(HttpMethod.Put, "/dashboard/v1/cache/entry", content: Json(new
        {
            type = typeof(System.IO.FileInfo).AssemblyQualifiedName,
            groupId = "",
            key = "k",
            value = "/etc/passwd",
        })));
        Assert.Equal(HttpStatusCode.BadRequest, arbitrary.StatusCode);

        using var delete = await host.Http.SendAsync(Req(HttpMethod.Delete,
            "/dashboard/v1/cache/entry?Type=" + Uri.EscapeDataString(typeof(System.Diagnostics.Process).AssemblyQualifiedName!) + "&Key=k"));
        Assert.Equal(HttpStatusCode.BadRequest, delete.StatusCode);

        // A type that is in the cache can be edited.
        await host.Cache.SaveAsync("p1", new DashboardCacheProbe { Name = "before" });
        using var edit = await host.Http.SendAsync(Req(HttpMethod.Put, "/dashboard/v1/cache/entry", content: Json(new
        {
            type = typeof(DashboardCacheProbe).AssemblyQualifiedName,
            groupId = "",
            key = "p1",
            value = new { name = "after" },
        })));
        Assert.Equal(HttpStatusCode.NoContent, edit.StatusCode);
        Assert.Equal("after", (await host.Cache.GetAsync<DashboardCacheProbe>("p1"))!.Name);
    }

    [Fact]
    public async Task Api_lab_http_calls_ignore_the_Host_header()
    {
        await using var host = await Start(new()
        {
            ["altruist:dashboard:enabled"] = "true",
            ["altruist:dashboard:token"] = Token,
        });

        var req = Req(HttpMethod.Post, "/dashboard/v1/lab/invoke", content: new StringContent(
            JsonSerializer.Serialize(new { kind = "http", method = "GET", path = "/dashboard/v1/cache/info" }), Encoding.UTF8, "application/json"));
        req.Headers.Host = "attacker.invalid:9";
        using var res = await host.Http.SendAsync(req);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var result = JsonDocument.Parse(await res.Content.ReadAsStringAsync()).RootElement;
        // The call reached this server (which answers 401: the lab forwards no dashboard credentials),
        // not attacker.invalid (which would be a connection error with no status).
        Assert.Equal(401, result.GetProperty("statusCode").GetInt32());

        var bad = Req(HttpMethod.Post, "/dashboard/v1/lab/invoke", content: new StringContent(
            JsonSerializer.Serialize(new { kind = "http", method = "GET", path = "/\\attacker.invalid/x" }), Encoding.UTF8, "application/json"));
        using var badRes = await host.Http.SendAsync(bad);
        var badResult = JsonDocument.Parse(await badRes.Content.ReadAsStringAsync()).RootElement;
        Assert.False(badResult.GetProperty("success").GetBoolean());
    }
}
