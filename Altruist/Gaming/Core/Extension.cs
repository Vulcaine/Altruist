using Altruist.Gaming;
using Microsoft.Extensions.DependencyInjection;

public static class AltruistGamingServiceCollectionExtensions
{
    public static IServiceCollection AddGamingSupport(this IServiceCollection services)
    {
        services.AddSingleton<IWorldPartitioner>(sp =>
        {
            return new WorldPartitioner(64, 64);
        });
        services.AddSingleton<GameWorldCoordinator>();
        services.AddSingleton(typeof(IPlayerService<>), typeof(AltruistPlayerService<>));
        return services;
    }
}
