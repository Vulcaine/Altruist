using Altruist.Contracts;
using Microsoft.Extensions.DependencyInjection;

namespace Altruist.EFCore;

public sealed class EFCoreConfiguration : IDatabaseConfiguration
{
    public string DatabaseName => "EFCore";

    public void Configure(IServiceCollection services)
    {
 
    }
}

public sealed class EFCoreToken : IDatabaseServiceToken
{
    public IDatabaseConfiguration Configuration => new EFCoreConfiguration();
}