using Altruist.Security;
using Altruist.Database;
using Altruist.ScyllaDB;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.DependencyInjection;

[ApiController]
[Route("/altruist/auth")]
public class MyController : JwtAuthController
{
    public MyController(JwtTokenIssuer jwtIssuer, ILoggerFactory loggerFactory, IServiceProvider serviceProvider) : base(jwtIssuer, loggerFactory, serviceProvider)
    {
    }

    public override ILoginService LoginService(IServiceProvider serviceProvider)
    {
        var factory = serviceProvider.GetRequiredService<VaultRepositoryFactory>();
        var defaultKeyspace = factory.Make<DefaultScyllaKeyspace>(ScyllaDBToken.Instance);
        var vault = defaultKeyspace.Select<MyAccount>();
        return new UsernamePasswordLoginService<MyAccount>(vault, new BcryptPasswordHasher());
    }
}