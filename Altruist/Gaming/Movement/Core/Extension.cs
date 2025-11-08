using Altruist.Contracts;
using Microsoft.Extensions.DependencyInjection;

namespace Altruist.Gaming.Movement;


public class MovementConfig : IAltruistConfiguration
{
    public void Configure(IServiceCollection services)
    {
        // services.AddSingleton<MovementPortalContext>();
    }
}