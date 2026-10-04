/*
Copyright 2025 Aron Gere
Licensed under the Apache License, Version 2.0
*/

using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;

using Altruist;
using Altruist.Security;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;

using Moq;

namespace Tests.Altruist.Security;

/// <summary>
/// JwtAuth used to parse the token before validating it (malformed input threw instead of
/// failing auth); ShieldAttribute failed open (an unresolvable or throwing handler let the
/// request through).
/// </summary>
public sealed class JwtAuthRegressionTests
{
    private static HttpAuthContext ContextWithToken(string? token)
    {
        var http = new DefaultHttpContext();
        if (token is not null)
            http.Request.Headers.Authorization = "Bearer " + token;
        return new HttpAuthContext(http);
    }

    private static JwtAuth NewAuth(Func<string, Task<ClaimsPrincipal?>> validate)
    {
        var validator = new Mock<IJwtTokenValidator>();
        validator.Setup(v => v.ValidateToken(It.IsAny<string>())).Returns(validate);
        return new JwtAuth(validator.Object, new ServiceCollection().BuildServiceProvider());
    }

    private static string SignedToken(DateTime expires)
    {
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(new string('k', 64)));
        var handler = new JwtSecurityTokenHandler();
        return handler.WriteToken(handler.CreateToken(new SecurityTokenDescriptor
        {
            Subject = new ClaimsIdentity(new[] { new Claim(JwtRegisteredClaimNames.Sub, "principal-1") }),
            Expires = expires,
            NotBefore = expires.AddHours(-2),
            IssuedAt = expires.AddHours(-2),
            SigningCredentials = new SigningCredentials(key, SecurityAlgorithms.HmacSha256),
        }));
    }

    [Theory]
    [InlineData("garbage")]
    [InlineData("a.b.c")]
    [InlineData("eyJhbGciOiJIUzI1NiJ9.not-base64!.x")]
    public async Task Malformed_tokens_fail_authentication_instead_of_throwing(string token)
    {
        // The validator rejects them (returns null) — before the fix the token was parsed first and
        // ReadJwtToken threw out of HandleAuthAsync.
        var auth = NewAuth(_ => Task.FromResult<ClaimsPrincipal?>(null));
        var result = await auth.HandleAuthAsync(ContextWithToken(token));
        Assert.False(result.AuthorizationResult.Succeeded);
    }

    [Fact]
    public async Task Validator_ArgumentException_is_an_authentication_failure()
    {
        var auth = NewAuth(_ => throw new ArgumentException("IDX12709: malformed"));
        var result = await auth.HandleAuthAsync(ContextWithToken("x.y.z"));
        Assert.False(result.AuthorizationResult.Succeeded);
    }

    [Fact]
    public async Task Validator_SecurityTokenException_is_an_authentication_failure()
    {
        var auth = NewAuth(_ => throw new SecurityTokenExpiredException("expired"));
        var result = await auth.HandleAuthAsync(ContextWithToken(SignedToken(DateTime.UtcNow.AddMinutes(10))));
        Assert.False(result.AuthorizationResult.Succeeded);
    }

    [Fact]
    public async Task Missing_token_fails()
    {
        var auth = NewAuth(_ => throw new InvalidOperationException("must not be called"));
        Assert.False((await auth.HandleAuthAsync(ContextWithToken(null))).AuthorizationResult.Succeeded);
    }

    [Fact]
    public async Task Valid_token_succeeds_with_details_and_sets_the_user()
    {
        var token = SignedToken(DateTime.UtcNow.AddMinutes(10));
        var principal = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim("sub", "principal-1") }, "jwt"));
        var auth = NewAuth(_ => Task.FromResult<ClaimsPrincipal?>(principal));
        var ctx = ContextWithToken(token);

        var result = await auth.HandleAuthAsync(ctx);

        Assert.True(result.AuthorizationResult.Succeeded);
        Assert.NotNull(result.AuthDetails);
        Assert.Same(principal, ctx.HttpContext.User);
    }
}

public sealed class ShieldAttributeRegressionTests
{
    public sealed class DenyAll : IShieldAuth
    {
        public Task<AuthResult> HandleAuthAsync(IAuthContext context) =>
            Task.FromResult(new AuthResult(AuthorizationResult.Failed(), null));
    }

    public sealed class AllowAll : IShieldAuth
    {
        public Task<AuthResult> HandleAuthAsync(IAuthContext context) =>
            Task.FromResult(new AuthResult(AuthorizationResult.Success(), null));
    }

    public sealed class Throwing : IShieldAuth
    {
        public Task<AuthResult> HandleAuthAsync(IAuthContext context) => throw new InvalidOperationException("handler bug");
    }

    public interface IUnregisteredDependency { }

    /// <summary>Cannot be constructed from DI: its dependency is not registered.</summary>
    public sealed class NeedsMissingDependency : IShieldAuth
    {
        public NeedsMissingDependency(IUnregisteredDependency dep) { }
        public Task<AuthResult> HandleAuthAsync(IAuthContext context) =>
            Task.FromResult(new AuthResult(AuthorizationResult.Success(), null));
    }

    private static AuthorizationFilterContext FilterContext(IServiceProvider services)
    {
        var http = new DefaultHttpContext { RequestServices = services };
        return new AuthorizationFilterContext(new ActionContext(http, new RouteData(), new ActionDescriptor()), new List<IFilterMetadata>());
    }

    private static IServiceProvider Services(Action<IServiceCollection>? configure = null)
    {
        var services = new ServiceCollection();
        configure?.Invoke(services);
        return services.BuildServiceProvider();
    }

    [Fact]
    public async Task Handler_registered_only_by_interface_is_still_applied()
    {
        // Before the fix GetService(typeof(DenyAll)) returned null and the request went through.
        var ctx = FilterContext(Services(s => s.AddSingleton<IShieldAuth, AllowAll>()));
        await new ShieldAttribute(typeof(DenyAll)).OnAuthorizationAsync(ctx);
        Assert.IsType<UnauthorizedResult>(ctx.Result);
    }

    [Fact]
    public async Task Handler_that_cannot_be_constructed_denies_the_request()
    {
        var ctx = FilterContext(Services());
        await new ShieldAttribute(typeof(NeedsMissingDependency)).OnAuthorizationAsync(ctx);
        Assert.IsType<UnauthorizedResult>(ctx.Result);
    }

    [Fact]
    public async Task Handler_that_throws_denies_the_request_and_leaves_no_auth_result()
    {
        var ctx = FilterContext(Services());
        await new ShieldAttribute(typeof(Throwing)).OnAuthorizationAsync(ctx);
        Assert.IsType<UnauthorizedResult>(ctx.Result);
        Assert.False(ctx.HttpContext.Items.ContainsKey("AuthResult"));
    }

    [Fact]
    public async Task Registered_allowing_handler_lets_the_request_through()
    {
        var ctx = FilterContext(Services(s => s.AddSingleton<AllowAll>()));
        await new ShieldAttribute(typeof(AllowAll)).OnAuthorizationAsync(ctx);
        Assert.Null(ctx.Result);
        Assert.IsType<AuthResult>(ctx.HttpContext.Items["AuthResult"]);
    }

    [Fact]
    public async Task Shield_without_handler_type_is_a_no_op()
    {
        var ctx = FilterContext(Services());
        await new ShieldAttribute().OnAuthorizationAsync(ctx);
        Assert.Null(ctx.Result);
    }

    [Fact]
    public async Task Non_http_authentication_fails_closed()
    {
        var services = Services();
        var auth = new HttpAuthContext(new DefaultHttpContext());

        Assert.Null(await new ShieldAttribute(typeof(Throwing)).AuthenticateNonHttpAsync(services, auth));
        Assert.Null(await new ShieldAttribute(typeof(DenyAll)).AuthenticateNonHttpAsync(services, auth));
        Assert.Null(await new ShieldAttribute(typeof(NeedsMissingDependency)).AuthenticateNonHttpAsync(services, auth));
    }
}
