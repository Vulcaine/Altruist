/* 
Copyright 2025 Aron Gere

Licensed under the Apache License, Version 2.0 (the "License");
you may not use this file except in compliance with the License.
You may obtain a copy of the License at

    http://www.apache.org/licenses/LICENSE-2.0

Unless required by applicable law or agreed to in writing, software
distributed under the License is distributed on an "AS IS" BASIS,
WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
See the License for the specific language governing permissions and
limitations under the License.
*/

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

    protected override ILoginService LoginService(IServiceProvider serviceProvider)
    {
        var factory = serviceProvider.GetRequiredService<VaultRepositoryFactory>();
        var defaultKeyspace = factory.Make<DefaultScyllaKeyspace>(ScyllaDBToken.Instance);
        var vault = defaultKeyspace.Select<MyAccount>();
        return new UsernamePasswordLoginService<MyAccount>(vault, new BcryptPasswordHasher());
    }
}