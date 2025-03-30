
using StackExchange.Redis;

namespace Altruist.Redis;

public class RedisDirectRouter : DirectRouter
{
    public RedisDirectRouter(IConnectionStore store,
        IMessageCodec codec,
        RedisSocketClientSender clientSender,
        RoomSender roomSender,
        BroadcastSender broadcastSender,
        ClientSynchronizator clientSynchronizator) : base(store, codec, clientSender, roomSender, broadcastSender, clientSynchronizator)
    {
    }
}

public class RedisEngineRouter : EngineRouter
{
    public RedisEngineRouter(IConnectionStore store,
    IMessageCodec codec,
    RedisEngineClientSender clientSender,
    RoomSender roomSender,
    BroadcastSender broadcastSender,
    ClientSynchronizator clientSynchronizator,
    IAltruistEngine engine) : base(store, codec, clientSender, roomSender, broadcastSender, clientSynchronizator, engine)
    {
    }
}

public class RedisSocketClientSender : ClientSender
{
    private readonly IConnectionMultiplexer _mux;
    private readonly ISubscriber _redisPublisher;
    private readonly ClientSender _underlying;

    RedisChannel channel = RedisChannel.Literal(OutgressRedis.MessageDistributeChannel);

    public RedisSocketClientSender(
    IConnectionStore store, IMessageCodec codec, IConnectionMultiplexer mux, ClientSender clientSender) : base(store, codec)
    {
        _mux = mux;
        _redisPublisher = mux.GetSubscriber();
        _underlying = clientSender;
    }

    public override async Task SendAsync<TPacketBase>(string clientId, TPacketBase message)
    {
        var socket = await _store.GetConnection(clientId);

        if (socket != null && socket.IsConnected)
        {
            await _underlying.SendAsync(clientId, message);
        }
        else
        {
            var redisMessage = _codec.Encoder.Encode(message);
            await _mux.GetDatabase().ListLeftPushAsync(IngressRedis.MessageQueue, redisMessage);
            // just publishing an empty message this way we are notifying all subscribers that there are messages in the queue.
            await _redisPublisher.PublishAsync(channel, "", CommandFlags.FireAndForget);
        }
    }
}

public class RedisEngineClientSender : EngineClientSender
{
    private readonly ISubscriber _redisPublisher;
    private readonly IConnectionMultiplexer _mux;

    RedisChannel channel = RedisChannel.Literal(OutgressRedis.MessageDistributeChannel);

    public RedisEngineClientSender(IConnectionStore store, IMessageCodec codec, IConnectionMultiplexer mux, IAltruistEngine engine) : base(store, codec, engine)
    {
        _redisPublisher = mux.GetSubscriber();
        _mux = mux;
    }

    public override async Task SendAsync<TPacket>(string clientId, TPacket message)
    {
        var socket = await _store.GetConnection(clientId);

        if (socket != null && socket.IsConnected)
        {
            await base.SendAsync(clientId, message);
        }
        else
        {
            var redisMessage = _codec.Encoder.Encode(message);
            await _mux.GetDatabase().ListLeftPushAsync(IngressRedis.MessageQueue, redisMessage);
            // just publishing an empty message this way we are notifying all subscribers that there are messages in the queue.
            await _redisPublisher.PublishAsync(channel, "", CommandFlags.FireAndForget);
        }
    }
}