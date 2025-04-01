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
}