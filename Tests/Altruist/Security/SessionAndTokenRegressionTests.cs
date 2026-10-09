/*
Copyright 2025 Aron Gere
Licensed under the Apache License, Version 2.0
*/

using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Security.Claims;

using Altruist;
using Altruist.InMemory;
using Altruist.Security;
using Altruist.Security.Auth;
using Altruist.Security.Http;

using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace Tests.Altruist.Security;

/// <summary>Session-mode authentication: sessions saved under a group, lifetimes, IP binding.</summary>
public sealed class SessionTokenAuthRegressionTests
{
    private static readonly IPAddress ClientIp = IPAddress.Parse("10.0.0.7");

    private static AuthTokenSessionModel Session(string token, TimeSpan validationInterval) => new()
    {
        StorageId = token,
        AccessToken = token,
        RefreshToken = Guid.NewGuid() + ";session",
        PrincipalId = "p-1",
        Ip = ClientIp.ToString(),
        AccessExpiration = DateTime.UtcNow.AddHours(1),
        RefreshExpiration = DateTime.UtcNow.AddDays(7),
        CacheValidationInterval = validationInterval,
    };

    private static SocketAuthContext Context(string token, IPAddress? ip = null) =>
        new() { Token = token, ClientIp = ip ?? ClientIp };

    private static SessionTokenAuth Auth(TokenSessionSyncService sync) => new(sync, NullLogger<SessionTokenAuth>.Instance);

    [Fact]
    public async Task Session_stays_valid_for_its_issued_lifetime_after_the_validation_interval()
    {
        var sync = new TokenSessionSyncService(new InMemoryCache());
        var token = Guid.NewGuid() + ";session";
        await sync.SaveAsync(Session(token, TimeSpan.FromMilliseconds(1)));
        var auth = Auth(sync);

        Assert.True((await auth.HandleAuthAsync(Context(token))).AuthorizationResult.Succeeded);
        await Task.Delay(30);
        var again = await auth.HandleAuthAsync(Context(token));

        Assert.True(again.AuthorizationResult.Succeeded);
        Assert.InRange(again.AuthDetails!.TimeLeftSeconds(), 3500, 3600);
    }

    [Fact]
    public async Task Session_saved_under_a_group_key_authenticates_with_that_group()
    {
        var sync = new TokenSessionSyncService(new InMemoryCache());
        var token = Guid.NewGuid() + ";session";
        await sync.SaveAsync(Session(token, TimeSpan.FromSeconds(10)), "group-of-p-1");

        var result = await Auth(sync).HandleAuthAsync(Context(token));

        Assert.True(result.AuthorizationResult.Succeeded);
        Assert.Equal("p-1", result.AuthDetails!.PrincipalId);
        Assert.Equal("group-of-p-1", result.AuthDetails.GroupKey);
        Assert.NotNull(await new SessionTokenValidator(sync).ValidateToken(token));
    }

    [Fact]
    public async Task Deleted_session_token_no_longer_resolves()
    {
        var sync = new TokenSessionSyncService(new InMemoryCache());
        var token = Guid.NewGuid() + ";session";
        await sync.SaveAsync(Session(token, TimeSpan.FromSeconds(10)), "g");
        await sync.DeleteAsync(token, "g");

        Assert.Null(await sync.FindByTokenAsync(token));
    }

    [Fact]
    public async Task Recently_validated_session_still_rejects_another_address()
    {
        var sync = new TokenSessionSyncService(new InMemoryCache());
        var token = Guid.NewGuid() + ";session";
        await sync.SaveAsync(Session(token, TimeSpan.FromMinutes(5)));
        var auth = Auth(sync);

        Assert.True((await auth.HandleAuthAsync(Context(token))).AuthorizationResult.Succeeded);
        Assert.False((await auth.HandleAuthAsync(Context(token, IPAddress.Parse("10.9.9.9")))).AuthorizationResult.Succeeded);
    }

    [Fact]
    public void Http_context_token_is_the_bearer_credential_without_its_scheme()
    {
        var http = new DefaultHttpContext();
        http.Request.Headers.Authorization = "Bearer abc;session";
        Assert.Equal("abc;session", new HttpAuthContext(http).Token);
        Assert.Equal("", new HttpAuthContext(new DefaultHttpContext()).ClientId);
    }
}

/// <summary>The legacy JWT pair issuer and the <see cref="JwtAuthController"/> login and refresh flow.</summary>
public sealed class JwtTokenPairRegressionTests
{
    private static readonly IPAddress ClientIp = IPAddress.Parse("10.0.0.8");

    private sealed class StaticMonitor : IOptionsMonitor<JwtBearerOptions>
    {
        private readonly JwtBearerOptions _options;
        public StaticMonitor(JwtBearerOptions options) => _options = options;
        public JwtBearerOptions CurrentValue => _options;
        public JwtBearerOptions Get(string? name) => _options;
        public IDisposable? OnChange(Action<JwtBearerOptions, string?> listener) => null;
    }

    private static readonly TokenValidationParameters Validation = AccessTokenTests.Validation();

    private static JwtTokenIssuer Issuer(double accessMinutes = 60, double refreshMinutes = 10080) =>
        new(new StaticMonitor(new JwtBearerOptions { TokenValidationParameters = Validation }), accessMinutes, refreshMinutes);

    private static DateTime ExpiryOf(string tokenWithSuffix) =>
        new JwtSecurityTokenHandler().ReadJwtToken(tokenWithSuffix.Split(';')[0]).ValidTo;

    [Fact]
    public void Issued_pair_reports_the_real_expirations_and_the_refresh_token_outlives_the_access_token()
    {
        var issue = (TokenIssue)Issuer(accessMinutes: 15, refreshMinutes: 600).WithClaims(new[] { new Claim("sub", "p") }).Issue();

        Assert.Equal(ExpiryOf(issue.AccessToken), issue.AccessExpiration);
        Assert.Equal(ExpiryOf(issue.RefreshToken), issue.RefreshExpiration);
        Assert.InRange((issue.AccessExpiration - DateTime.UtcNow).TotalMinutes, 14, 15);
        Assert.InRange((issue.RefreshExpiration - DateTime.UtcNow).TotalMinutes, 599, 600);
    }

    [Fact]
    public void Default_refresh_lifetime_is_longer_than_the_access_lifetime()
    {
        var issue = (TokenIssue)new JwtTokenIssuer(new StaticMonitor(new JwtBearerOptions { TokenValidationParameters = Validation }))
            .WithClaims(new[] { new Claim("sub", "p") }).Issue();
        Assert.True(issue.RefreshExpiration > issue.AccessExpiration);
    }

    [Fact]
    public void Refresh_lifetime_not_longer_than_access_lifetime_is_rejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Issuer(accessMinutes: 60, refreshMinutes: 30));
        Assert.Throws<ArgumentOutOfRangeException>(() => Issuer().WithClaims(Array.Empty<Claim>()).SetRefreshTokenExpiry(TimeSpan.FromMinutes(5)));
    }

    private sealed class FixedLogin : ILoginService
    {
        public readonly EmailPasswordAccountModel Account = new() { Email = "ann@example.com", PasswordHash = "x" };
        public Task<LoginResult> LoginAsync(LoginRequest request) => Task.FromResult(new LoginResult(true, null, Account));
        public Task<SignupResult> SignupAsync(SignupRequest request) => throw new NotSupportedException();
    }

    private sealed class TestAuthController : JwtAuthController
    {
        public TestAuthController(ILoginService login, TokenSessionSyncService sync, JwtTokenIssuer issuer)
            : base(new JwtTokenValidator(Validation), login, sync, issuer, null!, NullLoggerFactory.Instance)
        {
            var http = new DefaultHttpContext();
            http.Connection.RemoteIpAddress = ClientIp;
            ControllerContext = new ControllerContext { HttpContext = http };
        }

        public void Present(string authorization) => HttpContext.Request.Headers.Authorization = authorization;
    }

    private static (TestAuthController Controller, TokenSessionSyncService Sync, FixedLogin Login) Controller()
    {
        var sync = new TokenSessionSyncService(new InMemoryCache());
        var login = new FixedLogin();
        return (new TestAuthController(login, sync, Issuer()), sync, login);
    }

    private static string Strip(string token) => token.Split(';')[0];

    private static AltruistLoginResponse Body(IActionResult result) =>
        Assert.IsType<AltruistLoginResponse>(Assert.IsType<OkObjectResult>(result).Value);

    [Fact]
    public async Task Username_login_tokens_can_be_refreshed()
    {
        var (controller, _, _) = Controller();
        var login = Body(await controller.UsernamePasswordLogin(new UsernamePasswordLoginRequest("ann", "pw")));

        controller.Present($"Bearer {login.AccessToken};{login.RefreshToken}");
        var refreshed = Body(await controller.Refresh());

        Assert.NotEqual(login.AccessToken, refreshed.AccessToken);
    }

    [Fact]
    public async Task An_expired_access_token_can_still_be_refreshed_while_the_session_is_valid()
    {
        var (controller, sync, login) = Controller();
        var groupKey = login.Account.StorageId;
        var claims = new[]
        {
            new Claim(ClaimTypes.Name, "ann@example.com"), new Claim("GroupKey", groupKey),
            new Claim(JwtRegisteredClaimNames.Sub, groupKey), new Claim("Ip", ClientIp.ToString()),
        };
        var expiredAccess = new JwtSecurityTokenHandler().WriteToken(new JwtSecurityToken("Altruist", "Altruist", claims,
            notBefore: DateTime.UtcNow.AddHours(-2), expires: DateTime.UtcNow.AddMinutes(-5),
            signingCredentials: new SigningCredentials(Validation.IssuerSigningKey, SecurityAlgorithms.HmacSha256)));
        var refresh = OpaqueToken.New();
        await sync.SaveAsync(new AuthTokenSessionModel
        {
            StorageId = expiredAccess + ";jwt", AccessToken = expiredAccess + ";jwt", RefreshToken = refresh + ";jwt",
            PrincipalId = groupKey, Ip = ClientIp.ToString(),
            AccessExpiration = DateTime.UtcNow.AddMinutes(-5), RefreshExpiration = DateTime.UtcNow.AddDays(1),
        }, groupKey);

        controller.Present($"Bearer {expiredAccess};jwt;{refresh};jwt");
        var refreshed = Body(await controller.Refresh());

        var principal = new JwtSecurityTokenHandler { MapInboundClaims = false }.ValidateToken(Strip(refreshed.AccessToken), Validation, out var token);
        Assert.True(token.ValidTo > DateTime.UtcNow);
        Assert.Equal(groupKey, principal.FindFirst("GroupKey")?.Value);
        Assert.Equal(groupKey, principal.FindFirst(JwtRegisteredClaimNames.Sub)?.Value);
        Assert.Single(((JwtSecurityToken)token).Claims, c => c.Type == JwtRegisteredClaimNames.Exp);
    }

    [Fact]
    public async Task A_forged_access_token_is_not_refreshed()
    {
        var (controller, _, _) = Controller();
        var login = Body(await controller.EmailPasswordLogin(new EmailPasswordLoginRequest("ann@example.com", "pw")));
        var parts = Strip(login.AccessToken).Split('.');
        var forged = $"{parts[0]}.{parts[1]}.{new string('A', parts[2].Length)}";

        controller.Present($"Bearer {forged};jwt;{login.RefreshToken}");
        Assert.IsType<UnauthorizedObjectResult>(await controller.Refresh());
    }
}

public sealed class SignupRequestTests
{
    [Fact]
    public void Either_username_or_email_is_enough()
    {
        Assert.Equal("ann", new SignupRequest("pw", username: "ann").Username);
        Assert.Equal("ann@example.com", new SignupRequest("pw", email: "ann@example.com").Email);
    }

    [Fact]
    public void Neither_username_nor_email_is_rejected()
    {
        Assert.Throws<BadHttpRequestException>(() => new SignupRequest("pw"));
    }
}

/// <summary><see cref="JwtAuth"/> with tokens that carry no Ip / GroupKey claims, and over non-HTTP contexts.</summary>
public sealed class JwtAuthDetailsTests
{
    private static readonly TokenValidationParameters Validation = AccessTokenTests.Validation();

    private static JwtAuth Auth() => new(new JwtTokenValidator(Validation), new ServiceCollection().BuildServiceProvider());

    [Fact]
    public async Task Access_token_issuer_tokens_yield_live_details()
    {
        var token = new AccessTokenIssuer(Validation, 15).Issue("p-7").Token;
        var http = new DefaultHttpContext();
        http.Request.Headers.Authorization = "Bearer " + token;
        http.Connection.RemoteIpAddress = IPAddress.Parse("10.1.2.3");

        var result = await Auth().HandleAuthAsync(new HttpAuthContext(http));

        Assert.True(result.AuthorizationResult.Succeeded);
        Assert.True(result.AuthDetails!.IsAlive());
        Assert.Equal("p-7", result.AuthDetails.PrincipalId);
        Assert.Equal("p-7", result.AuthDetails.GroupKey);
        Assert.Equal("10.1.2.3", result.AuthDetails.Ip);
    }

    [Fact]
    public async Task Socket_contexts_authenticate_with_their_token()
    {
        var token = new AccessTokenIssuer(Validation, 15).Issue("p-8").Token;
        var context = new SocketAuthContext { Token = token, ClientIp = IPAddress.Loopback };

        var result = await Auth().HandleAuthAsync(context);

        Assert.True(result.AuthorizationResult.Succeeded);
        Assert.Equal("p-8", result.AuthDetails!.PrincipalId);
        Assert.False((await Auth().HandleAuthAsync(new SocketAuthContext { Token = "garbage" })).AuthorizationResult.Succeeded);
    }
}

public sealed class ConnectionTicketConcurrencyTests
{
    /// <summary>A backplane whose calls take a network round trip (they complete asynchronously).</summary>
    private sealed class RoundTripBackplane : IFleetBackplane
    {
        private readonly InMemoryFleetBackplane _inner = new(shared: true);
        private static async Task Trip() => await Task.Delay(1);
        public string Kind => _inner.Kind;
        public bool Shared => true;
        public async Task SetAsync(string key, string value, TimeSpan ttl, CancellationToken ct = default) { await Trip(); await _inner.SetAsync(key, value, ttl, ct); }
        public async Task SetManyAsync(IReadOnlyCollection<KeyValuePair<string, string>> values, TimeSpan ttl, CancellationToken ct = default) { await Trip(); await _inner.SetManyAsync(values, ttl, ct); }
        public async Task<bool> SetIfAbsentAsync(string key, string value, TimeSpan ttl, CancellationToken ct = default) { await Trip(); return await _inner.SetIfAbsentAsync(key, value, ttl, ct); }
        public async Task<string?> GetAsync(string key, CancellationToken ct = default) { await Trip(); return await _inner.GetAsync(key, ct); }
        public async Task<string?> TakeAsync(string key, CancellationToken ct = default) { await Trip(); return await _inner.TakeAsync(key, ct); }
        public async Task<IReadOnlyDictionary<string, string>> GetByPrefixAsync(string prefix, CancellationToken ct = default) { await Trip(); return await _inner.GetByPrefixAsync(prefix, ct); }
        public async Task DeleteAsync(string key, CancellationToken ct = default) { await Trip(); await _inner.DeleteAsync(key, ct); }
        public async Task<bool> DeleteIfValueAsync(string key, string value, CancellationToken ct = default) { await Trip(); return await _inner.DeleteIfValueAsync(key, value, ct); }
    }

    [Fact]
    public async Task Concurrent_issues_on_several_servers_keep_at_most_max_per_principal_outstanding()
    {
        var backplane = new RoundTripBackplane();
        var servers = new[] { new ConnectionTicketService(backplane, null), new ConnectionTicketService(backplane, null) };

        var issued = await Task.WhenAll(Enumerable.Range(0, 16).Select(i => servers[i % 2].IssueAsync("p-1")));
        var redeemed = await Task.WhenAll(issued.Select(t => servers[0].RedeemAsync(t.Ticket)));

        Assert.InRange(redeemed.Count(r => r is not null), 1, new ConnectionTicketOptions().MaxPerPrincipal);
    }
}
