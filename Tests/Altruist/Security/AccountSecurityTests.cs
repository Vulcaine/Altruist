/*
Copyright 2025 Aron Gere
Licensed under the Apache License, Version 2.0
*/

using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Security.Claims;
using System.Text;

using Altruist;
using Altruist.Security;

using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace Tests.Altruist.Security;

public sealed class PasswordHasherTests
{
    [Fact]
    public void Defaults_are_unchanged_plain_bcrypt_work_factor_11()
    {
        var hasher = new BcryptPasswordHasher();
        var hash = hasher.Hash("correct horse");
        Assert.Equal("11", hash.Split('$')[2]);
        Assert.True(BCrypt.Net.BCrypt.Verify("correct horse", hash));
        Assert.True(hasher.Verify("correct horse", hash));
        Assert.False(hasher.Verify("correct horsE", hash));
        // Plain BCrypt only reads the first 72 bytes (unchanged default).
        Assert.True(hasher.Verify(new string('a', 72) + "2", hasher.Hash(new string('a', 72) + "1")));
    }

    [Fact]
    public void A_null_hash_costs_a_verify_and_fails_and_a_malformed_hash_fails()
    {
        var hasher = new BcryptPasswordHasher(4, "none");
        Assert.False(hasher.Verify("anything", null));
        Assert.False(hasher.Verify("anything", "not-a-bcrypt-hash"));
        Assert.False(hasher.Verify("anything", ""));
    }

    [Fact]
    public void Sha384_prehash_uses_enhanced_hashes_without_truncation()
    {
        var hasher = new BcryptPasswordHasher(5, "SHA384");
        Assert.True(hasher.Enhanced);
        var hash = hasher.Hash("pw 1");
        Assert.Equal("05", hash.Split('$')[2]);
        Assert.True(BCrypt.Net.BCrypt.EnhancedVerify("pw 1", hash));
        Assert.False(hasher.Verify(new string('a', 100) + "2", hasher.Hash(new string('a', 100) + "1")));
        // Hashes written by BCrypt.Net's enhanced API directly (existing data) verify.
        Assert.True(hasher.Verify("legacy", BCrypt.Net.BCrypt.EnhancedHashPassword("legacy", 4)));
        // The modes do not mix.
        Assert.False(hasher.Verify("pw 1", new BcryptPasswordHasher(5, "none").Hash("pw 1")));
        Assert.False(hasher.Verify("anything", null));
    }

    [Theory]
    [InlineData(3, "none")]
    [InlineData(32, "none")]
    [InlineData(10, "md5")]
    public void Bad_settings_are_rejected(int workFactor, string prehash) =>
        Assert.ThrowsAny<ArgumentException>(() => new BcryptPasswordHasher(workFactor, prehash));
}

public sealed class AccessTokenTests
{
    internal static TokenValidationParameters Validation(string key = "0123456789abcdef0123456789abcdef-key")
    {
        var p = new TokenValidationParameters
        {
            ValidIssuer = "Altruist", ValidAudience = "Altruist",
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(key)),
            ValidateIssuer = true, ValidateAudience = true, ValidateIssuerSigningKey = true, ValidateLifetime = true,
            ClockSkew = TimeSpan.Zero,
        };
        AuthConfiguration.Harden(p);
        return p;
    }

    [Fact]
    public void Issues_hs256_tokens_with_subject_claims_jti_and_lifetime()
    {
        var validation = Validation();
        var issuer = new AccessTokenIssuer(validation, 15);
        Assert.Equal(TimeSpan.FromMinutes(15), issuer.Lifetime);

        var issued = issuer.Issue("p-1", new[] { new Claim("name", "Ann"), new Claim("sub", "forged"), new Claim("role", "x") });
        Assert.Equal(900, issued.ExpiresInSeconds);

        var handler = new JwtSecurityTokenHandler { MapInboundClaims = false };
        var principal = handler.ValidateToken(issued.Token, validation, out var token);
        var jwt = (JwtSecurityToken)token;
        Assert.Equal("HS256", jwt.Header.Alg);
        Assert.Equal("p-1", principal.PrincipalId());
        Assert.Single(jwt.Claims, c => c.Type == "sub");
        Assert.Equal("Ann", principal.PrincipalName());
        Assert.Equal("x", principal.FindFirstValue("role"));
        Assert.False(string.IsNullOrEmpty(principal.FindFirstValue("jti")));
        Assert.InRange((jwt.ValidTo - jwt.ValidFrom).TotalMinutes, 14.9, 15.1);

        var shortLived = issuer.Issue("p-1", lifetime: TimeSpan.FromMinutes(1));
        Assert.Equal(60, shortLived.ExpiresInSeconds);
    }

    [Fact]
    public void Tokens_expire_and_need_the_key()
    {
        var validation = Validation();
        var handler = new JwtSecurityTokenHandler();
        var old = new AccessTokenIssuer(validation, TimeSpan.FromMinutes(15), () => DateTime.UtcNow.AddMinutes(-16)).Issue("p");
        Assert.Throws<SecurityTokenExpiredException>(() => handler.ValidateToken(old.Token, validation, out _));
        var other = new AccessTokenIssuer(Validation("another-key-another-key-another-key"), 15).Issue("p");
        Assert.ThrowsAny<SecurityTokenException>(() => handler.ValidateToken(other.Token, validation, out _));
    }

    [Fact]
    public void Hardened_validation_rejects_other_algorithms_and_tokens_without_expiry()
    {
        // 64 bytes: long enough to sign HS512, which the pinned validation must still refuse.
        var validation = Validation(new string('k', 64));
        var key = (SymmetricSecurityKey)validation.IssuerSigningKey;
        string Sign(string alg, DateTime? expires)
        {
            var h = new JwtSecurityTokenHandler { SetDefaultTimesOnTokenCreation = false };
            return h.WriteToken(h.CreateToken(new SecurityTokenDescriptor
            {
                Issuer = "Altruist", Audience = "Altruist", Expires = expires,
                Subject = new ClaimsIdentity(new[] { new Claim("sub", "p") }),
                SigningCredentials = new SigningCredentials(key, alg),
            }));
        }
        var handler = new JwtSecurityTokenHandler();
        var exp = DateTime.UtcNow.AddMinutes(5);
        handler.ValidateToken(Sign(SecurityAlgorithms.HmacSha256, exp), validation, out _);
        Assert.ThrowsAny<SecurityTokenException>(() => handler.ValidateToken(Sign(SecurityAlgorithms.HmacSha512, exp), validation, out _));
        Assert.ThrowsAny<SecurityTokenException>(() => handler.ValidateToken(Sign(SecurityAlgorithms.HmacSha256, null), validation, out _));
        var unsigned = new JwtSecurityTokenHandler().WriteToken(new JwtSecurityToken("Altruist", "Altruist", new[] { new Claim("sub", "p") }, DateTime.UtcNow, exp));
        Assert.ThrowsAny<SecurityTokenException>(() => handler.ValidateToken(unsigned, validation, out _));
    }

    [Fact]
    public async Task JwtTokenIssuer_WithClaims_no_longer_shares_claims_between_callers()
    {
        var options = new JwtBearerOptions { TokenValidationParameters = Validation() };
        var monitor = new StaticMonitor(options);
        var issuer = new JwtTokenIssuer(monitor);

        var scoped = issuer.WithClaims(new[] { new Claim("sub", "a") });
        Assert.NotSame(issuer, scoped);
        Assert.Equal("", ((TokenIssue)issuer.Issue()).PrincipalId);

        var subjects = await Task.WhenAll(Enumerable.Range(0, 200).Select(i => Task.Run(() =>
        {
            var issue = (TokenIssue)issuer.WithClaims(new[] { new Claim("sub", "p" + i) }).Issue();
            return (Expected: "p" + i, Actual: issue.PrincipalId);
        })));
        Assert.All(subjects, s => Assert.Equal(s.Expected, s.Actual));
    }

    private sealed class StaticMonitor : IOptionsMonitor<JwtBearerOptions>
    {
        private readonly JwtBearerOptions _options;
        public StaticMonitor(JwtBearerOptions options) => _options = options;
        public JwtBearerOptions CurrentValue => _options;
        public JwtBearerOptions Get(string? name) => _options;
        public IDisposable? OnChange(Action<JwtBearerOptions, string?> listener) => null;
    }
}

public sealed class AuthConfigurationTests
{
    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData(AuthConfiguration.DevelopmentKey, false)]
    [InlineData("short-secret", false)]
    [InlineData("a-long-random-production-secret-with-enough-bytes", true)]
    public void Key_problems(string? key, bool acceptable) => Assert.Equal(acceptable, AuthConfiguration.KeyProblem(key) is null);

    [Fact]
    public void The_minimum_key_length_is_configurable() =>
        Assert.NotNull(AuthConfiguration.KeyProblem(new string('k', 40), minKeyBytes: 64));

    [Fact]
    public void Production_with_security_configured_refuses_a_weak_key()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            AuthConfiguration.EnforceKeyPolicy(AuthConfiguration.DevelopmentKey, 32, securityConfigured: true, production: true, NullLogger.Instance));
        Assert.Contains("Refusing to start", ex.Message);
        // A strong key starts; other environments and apps without a security section only warn.
        AuthConfiguration.EnforceKeyPolicy(new string('k', 48), 32, securityConfigured: true, production: true, NullLogger.Instance);
        AuthConfiguration.EnforceKeyPolicy(AuthConfiguration.DevelopmentKey, 32, securityConfigured: true, production: false, NullLogger.Instance);
        AuthConfiguration.EnforceKeyPolicy(null, 32, securityConfigured: false, production: true, NullLogger.Instance);
    }

    private static JwtBearerOptions Bearer(IServiceCollection services)
    {
        services.AddLogging();
        new AuthConfiguration(new string('k', 48), "jwt", 32, mapInboundClaims: false, nameClaim: "name").Configure(services).GetAwaiter().GetResult();
        return services.BuildServiceProvider().GetRequiredService<IOptionsMonitor<JwtBearerOptions>>().Get(JwtBearerDefaults.AuthenticationScheme);
    }

    [Fact]
    public void Bearer_validation_is_hardened_and_claim_options_apply()
    {
        var options = Bearer(new ServiceCollection());
        Assert.Equal(new[] { SecurityAlgorithms.HmacSha256 }, options.TokenValidationParameters.ValidAlgorithms);
        Assert.True(options.TokenValidationParameters.RequireExpirationTime);
        Assert.True(options.TokenValidationParameters.RequireSignedTokens);
        Assert.Equal("name", options.TokenValidationParameters.NameClaimType);
        Assert.False(options.MapInboundClaims);
    }

    private sealed class JsonWriter : IAuthChallengeWriter
    {
        public Task WriteAsync(HttpContext context, int statusCode)
        {
            context.Response.StatusCode = statusCode;
            return context.Response.WriteAsync($"{{\"status\":{statusCode}}}");
        }
    }

    private static (HttpContext Http, MemoryStream Body) Request(IServiceProvider services)
    {
        var http = new DefaultHttpContext { RequestServices = services };
        var body = new MemoryStream();
        http.Response.Body = body;
        return (http, body);
    }

    [Fact]
    public async Task A_registered_challenge_writer_writes_401_and_403_bodies()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IAuthChallengeWriter, JsonWriter>();
        var options = Bearer(services);
        var sp = services.BuildServiceProvider();
        var scheme = new AuthenticationScheme(JwtBearerDefaults.AuthenticationScheme, null, typeof(JwtBearerHandler));

        var (http, body) = Request(sp);
        var challenge = new JwtBearerChallengeContext(http, scheme, options, new AuthenticationProperties());
        await options.Events.Challenge(challenge);
        Assert.True(challenge.Handled);
        Assert.Equal(401, http.Response.StatusCode);
        Assert.Equal("{\"status\":401}", Encoding.UTF8.GetString(body.ToArray()));

        (http, body) = Request(sp);
        await options.Events.Forbidden(new ForbiddenContext(http, scheme, options));
        Assert.Equal("{\"status\":403}", Encoding.UTF8.GetString(body.ToArray()));
    }

    [Fact]
    public async Task Without_a_writer_the_default_challenge_runs()
    {
        var services = new ServiceCollection();
        var options = Bearer(services);
        var (http, _) = Request(services.BuildServiceProvider());
        var challenge = new JwtBearerChallengeContext(http, new AuthenticationScheme("Bearer", null, typeof(JwtBearerHandler)), options, new AuthenticationProperties());
        await options.Events.Challenge(challenge);
        Assert.False(challenge.Handled);
    }
}

public sealed class OpaqueTokenAndExtensionTests
{
    [Fact]
    public void Tokens_are_256_bit_base64url_and_hashes_are_lowercase_sha256_hex()
    {
        var a = OpaqueToken.New();
        Assert.Equal(43, a.Length);
        Assert.NotEqual(a, OpaqueToken.New());
        Assert.True(OpaqueToken.IsWellFormed(a));
        Assert.Equal(32, Convert.FromBase64String(a.Replace('-', '+').Replace('_', '/') + "=").Length);
        Assert.Equal("ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad", OpaqueToken.Hash("abc"));
        Assert.False(OpaqueToken.IsWellFormed(null));
        Assert.False(OpaqueToken.IsWellFormed(a[..42]));
        Assert.False(OpaqueToken.IsWellFormed(a[..42] + "="));
        Assert.False(OpaqueToken.IsWellFormed("x' OR '1'='1"));
    }

    [Fact]
    public void Principal_id_and_name_read_raw_or_mapped_claims()
    {
        var raw = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim("sub", "p-1"), new Claim("name", "Ann") }, "jwt"));
        Assert.Equal("p-1", raw.PrincipalId());
        Assert.Equal("Ann", raw.PrincipalName());

        var mapped = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim(ClaimTypes.NameIdentifier, "p-2"), new Claim(ClaimTypes.Name, "Bo") }, "jwt"));
        Assert.Equal("p-2", mapped.PrincipalId());
        Assert.Equal("Bo", mapped.PrincipalName());

        Assert.Null(new ClaimsPrincipal(new ClaimsIdentity()).PrincipalId());
    }

    [Fact]
    public void Client_ip_is_the_connection_address()
    {
        var http = new DefaultHttpContext();
        Assert.Equal("unknown", http.ClientIp());
        http.Connection.RemoteIpAddress = IPAddress.Parse("203.0.113.7");
        Assert.Equal("203.0.113.7", http.ClientIp());
    }
}

public sealed class RefreshCookieTests
{
    private static IConfiguration Config(params (string Key, string Value)[] values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values.ToDictionary(v => v.Key, v => (string?)v.Value)).Build();

    [Fact]
    public void Defaults_and_configured_names()
    {
        var defaults = new RefreshCookie(Config());
        Assert.Equal("refresh_token", defaults.Name);
        Assert.Equal("/", defaults.Path);
        Assert.Equal(SameSiteMode.Strict, defaults.SameSite);

        var cookie = new RefreshCookie(Config(
            ("altruist:security:refresh-cookie:name", "app_rt"), ("altruist:security:refresh-cookie:path", "/api/auth"),
            ("altruist:security:refresh-cookie:same-site", "Lax"), ("altruist:security:refresh-cookie:csrf-header", "X-App")));
        Assert.Equal("app_rt", cookie.Name);
        Assert.Equal("/api/auth", cookie.Path);
        Assert.Equal(SameSiteMode.Lax, cookie.SameSite);

        var http = new DefaultHttpContext();
        Assert.False(cookie.HasCsrfHeader(http.Request));
        http.Request.Headers["X-App"] = "1";
        Assert.True(cookie.HasCsrfHeader(http.Request));

        cookie.Append(http.Response, "tok", DateTimeOffset.UtcNow.AddDays(1));
        var header = http.Response.Headers.SetCookie.ToString();
        Assert.StartsWith("app_rt=tok;", header);
        Assert.Contains("path=/api/auth", header);
        Assert.Contains("httponly", header);
        Assert.Contains("samesite=lax", header);
        Assert.DoesNotContain("secure", header);

        Assert.Throws<ArgumentException>(() => new RefreshCookie(Config(("altruist:security:refresh-cookie:same-site", "sometimes"))));
    }
}

public sealed class ConnectionTicketTests
{
    [Fact]
    public async Task Tickets_redeem_once_expire_and_are_capped_per_principal()
    {
        var now = DateTime.UtcNow;
        var tickets = new ConnectionTicketService(new InMemoryFleetBackplane(shared: false, () => now), new ConnectionTicketOptions());
        var t = await tickets.IssueAsync("p-1", "Ann");
        Assert.Equal(30, t.ExpiresInSeconds);
        Assert.Equal(new ConnectionTicketIdentity("p-1", "Ann"), await tickets.RedeemAsync(t.Ticket));
        Assert.Null(await tickets.RedeemAsync(t.Ticket));
        Assert.Null(await tickets.RedeemAsync("garbage"));
        Assert.Null(await tickets.RedeemAsync(null));

        var late = await tickets.IssueAsync("p-1");
        now = now.AddSeconds(31);
        Assert.Null(await tickets.RedeemAsync(late.Ticket));

        var issued = new List<string>();
        for (var i = 0; i < 6; i++)
            issued.Add((await tickets.IssueAsync("p-2")).Ticket);
        Assert.Null(await tickets.RedeemAsync(issued[0]));
        Assert.Null(await tickets.RedeemAsync(issued[1]));
        Assert.Equal("p-2", (await tickets.RedeemAsync(issued[^1]))!.GroupKey);
    }

    [Fact]
    public async Task A_ticket_from_one_server_redeems_once_on_another()
    {
        var shared = new InMemoryFleetBackplane(shared: true);
        var a = new ConnectionTicketService(shared, null);
        var b = new ConnectionTicketService(shared, null);
        var t = await a.IssueAsync("p-3", "Fleet");
        var results = await Task.WhenAll(Enumerable.Range(0, 32).Select(i => Task.Run(() => (i % 2 == 0 ? a : b).RedeemAsync(t.Ticket))));
        Assert.Single(results, r => r is not null);
    }

    [Fact]
    public async Task The_shield_reads_the_configured_query_parameter()
    {
        var cfg = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["altruist:security:tickets:query-param"] = "t",
            ["altruist:security:tickets:ttl-seconds"] = "10",
            ["altruist:security:tickets:session-hours"] = "2",
        }).Build();
        var tickets = new ConnectionTicketService(new InMemoryFleetBackplane(), ConnectionTicketOptions.FromConfiguration(cfg));
        var auth = new TicketShieldAuth(tickets);
        var t = await tickets.IssueAsync("p-9", "Drifter");
        Assert.Equal(10, t.ExpiresInSeconds);

        Task<AuthResult> Run(string query)
        {
            var http = new DefaultHttpContext();
            http.Request.QueryString = new QueryString(query);
            http.Connection.RemoteIpAddress = IPAddress.Loopback;
            return auth.HandleAuthAsync(new HttpAuthContext(http));
        }

        Assert.False((await Run("?ticket=" + t.Ticket)).AuthorizationResult.Succeeded);
        var ok = await Run("?t=" + Uri.EscapeDataString(t.Ticket));
        Assert.True(ok.AuthorizationResult.Succeeded);
        Assert.Equal("p-9", ok.AuthDetails!.PrincipalId);
        Assert.Equal("Drifter", ok.AuthDetails.GroupKey);
        Assert.Equal("127.0.0.1", ok.AuthDetails.Ip);
        Assert.InRange(ok.AuthDetails.TimeLeftSeconds(), 7100, 7200);
        Assert.False((await Run("?t=" + Uri.EscapeDataString(t.Ticket))).AuthorizationResult.Succeeded);
    }

    [Fact]
    public void Redaction_keeps_only_warnings_of_the_hosting_request_log()
    {
        var services = new ServiceCollection();
        TicketLogRedactionConfiguration.Redact(services);
        var options = services.BuildServiceProvider().GetRequiredService<IOptions<LoggerFilterOptions>>().Value;
        Assert.Contains(options.Rules, r => r.CategoryName == TicketLogRedactionConfiguration.HostingDiagnostics && r.LogLevel == LogLevel.Warning);
    }
}
