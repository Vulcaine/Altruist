namespace Altruist.Redis;

public static class Extensions
{
    public static AltruistDatabaseBuilder WithRedis(this AltruistCacheBuilder builder)
    {
        return builder.SetupCache<RedisConnectionSetup>(RedisCacheServiceToken.Instance);
    }

    public static AltruistDatabaseBuilder WithRedis(this AltruistCacheBuilder builder, Func<RedisConnectionSetup, RedisConnectionSetup>? setup)
    {
        return builder.SetupCache(RedisCacheServiceToken.Instance, setup);
    }
}