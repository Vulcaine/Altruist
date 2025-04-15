using Altruist.Auth;
using Altruist.Database;
using Altruist.ScyllaDB;
using Microsoft.AspNetCore.Mvc;

[ApiController]
[Route("/altruist/auth")]
public class MyController : AuthController
{
    public MyController(VaultRepositoryFactory factory, JwtTokenIssuer jwtIssuer, IServiceProvider serviceProvider) : base(factory, jwtIssuer, serviceProvider)
    {
    }

    public override ILoginService LoginService(VaultRepositoryFactory factory)
    {
        var defaultKeyspace = factory.Make<DefaultScyllaKeyspace>(ScyllaDBToken.Instance);
        var vault = defaultKeyspace.Select<MyAccount>();
        return new UsernamePasswordLoginService<MyAccount>(vault);
    }
}