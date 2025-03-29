namespace Altruist.Redis;

public static class IngressRedis
{
    public const string MessageDistributeChannel = "distribute-message";
    public const string MessageQueue = "message-queue";
}

public static class OutgressRedis
{
    public const string MessageDistributeChannel = "distribute-message";
}