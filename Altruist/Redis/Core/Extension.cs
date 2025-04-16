using System.Reflection;
using Altruist.UORM;

namespace Altruist.Redis;

public static class Extensions
{
    public static AltruistDatabaseBuilder WithRedis(this IAfterConnectionBuilder builder)
    {
        return builder.SetupCache<RedisConnectionSetup>(RedisCacheServiceToken.Instance);
    }

    public static AltruistDatabaseBuilder WithRedis(this IAfterConnectionBuilder builder, Func<RedisConnectionSetup, RedisConnectionSetup>? setup)
    {
        return builder.SetupCache(RedisCacheServiceToken.Instance, setup);
    }

    public static RedisConnectionSetup ForgeDocuments(this RedisConnectionSetup setup)
    {
        var modelTypes = AppDomain.CurrentDomain.GetAssemblies()
            .SelectMany(a => a.GetTypes())
            .Where(t => typeof(IStoredModel).IsAssignableFrom(t) && !t.IsAbstract && t.IsClass);

        foreach (var type in modelTypes)
        {
            setup.AddDocument(type);
        }

        return setup;
    }

}