using System.Runtime.CompilerServices;
using Altruist.Contracts;
using Microsoft.Extensions.DependencyInjection;

namespace Altruist.Gaming.Movement;

public static class ModuleInitializer
{
    [ModuleInitializer]
    public static void Init()
    {
        ServiceConfig.Register(new MovementConfig());
    }
}

public class MovementConfig : IConfiguration
{
    public void Configure(IServiceCollection services)
    {
        services.AddSingleton<MovementPortalContext>();
    }
}