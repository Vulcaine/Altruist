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

using Altruist.Engine;

using StackExchange.Redis;

namespace Altruist.Redis;

/// <summary>
/// A <see cref="DirectRouter"/> whose per-client sender is <see cref="RedisSocketClientSender"/>: packets for
/// clients connected to another process are pushed onto Redis instead of being dropped.
/// </summary>
/// <remarks>
/// Not registered in DI by this package (no <c>[Service]</c> attribute); construct or register it yourself.
/// For a single process use the regular router; for shared fleet state (registry, claims, counters) use
/// <see cref="RedisFleetBackplane"/>, which is a key/value store, not a packet relay.
/// </remarks>
public class RedisDirectRouter : DirectRouter
{
    /// <summary>Creates the router; all arguments are passed to <see cref="DirectRouter"/>.</summary>
    /// <param name="store">Connection store used to look clients up.</param>
    /// <param name="codec">Packet codec.</param>
    /// <param name="clientSender">Redis-aware single-client sender.</param>
    /// <param name="roomSender">Room sender.</param>
    /// <param name="broadcastSender">Broadcast sender.</param>
    /// <param name="clientSynchronizator">Client synchronizer.</param>
    public RedisDirectRouter(IConnectionStore store,
        ICodec codec,
        RedisSocketClientSender clientSender,
        RoomSender roomSender,
        BroadcastSender broadcastSender,
        ClientSynchronizator clientSynchronizator) : base(store, codec, clientSender, roomSender, broadcastSender, clientSynchronizator)
    {
    }
}

/// <summary>
/// An <see cref="EngineRouter"/> whose per-client sender is <see cref="RedisEngineClientSender"/>: packets for
/// clients connected to another process are wrapped in an <see cref="InterprocessPacket"/> and pushed onto Redis.
/// </summary>
/// <remarks>Not registered in DI by this package (no <c>[Service]</c> attribute); construct or register it yourself.</remarks>
public class RedisEngineRouter : EngineRouter
{
    /// <summary>Creates the router; all arguments are passed to <see cref="EngineRouter"/>.</summary>
    /// <param name="store">Connection store used to look clients up.</param>
    /// <param name="codec">Packet codec.</param>
    /// <param name="clientSender">Redis-aware engine client sender.</param>
    /// <param name="roomSender">Room sender.</param>
    /// <param name="broadcastSender">Broadcast sender.</param>
    /// <param name="clientSynchronizator">Client synchronizer.</param>
    /// <param name="engine">Engine that runs scheduled tasks.</param>
    public RedisEngineRouter(IConnectionStore store,
    ICodec codec,
    RedisEngineClientSender clientSender,
    RoomSender roomSender,
    BroadcastSender broadcastSender,
    ClientSynchronizator clientSynchronizator,
    IAltruistEngine engine) : base(store, codec, clientSender, roomSender, broadcastSender, clientSynchronizator, engine)
    {
    }
}

/// <summary>
/// Single-client sender that delivers locally when the client is connected to this process, and otherwise
/// encodes the packet, <c>LPUSH</c>es it onto the Redis list <see cref="IngressRedis.MessageQueue"/> and
/// publishes an empty fire-and-forget notification on <see cref="OutgressRedis.MessageDistributeChannel"/>.
/// </summary>
/// <remarks>
/// The queued payload carries no recipient id. This package contains no consumer of that list/channel;
/// a receiving process must supply one. Each remote send costs two Redis commands.
/// </remarks>
public class RedisSocketClientSender : ClientSender
{
    private readonly IConnectionMultiplexer _mux;
    private readonly ISubscriber _redisPublisher;
    private readonly ClientSender _underlying;

    RedisChannel channel = RedisChannel.Literal(OutgressRedis.MessageDistributeChannel);

    /// <summary>Creates the sender.</summary>
    /// <param name="store">Connection store used to decide local vs remote delivery.</param>
    /// <param name="codec">Encodes packets queued to Redis.</param>
    /// <param name="mux">Redis connection used for the list push and publish.</param>
    /// <param name="clientSender">Sender used for clients connected to this process.</param>
    public RedisSocketClientSender(
    IConnectionStore store, ICodec codec, IConnectionMultiplexer mux, ClientSender clientSender) : base(store, codec)
    {
        _mux = mux;
        _redisPublisher = mux.GetSubscriber();
        _underlying = clientSender;
    }

    /// <summary>
    /// Sends through the local sender when <paramref name="clientId"/> has a connected socket here;
    /// otherwise queues the encoded packet on Redis (network I/O) as described on the type.
    /// </summary>
    /// <param name="clientId">Target client id.</param>
    /// <param name="message">Packet to send.</param>
    public override async Task SendAsync<TPacketBase>(string clientId, TPacketBase message)
    {
        var socket = await _store.GetConnectionAsync(clientId);

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

/// <summary>
/// Engine-aware variant of <see cref="RedisSocketClientSender"/>: local clients go through
/// <see cref="EngineClientSender"/>; for others the packet is wrapped in an <see cref="InterprocessPacket"/>
/// tagged with this process's id, encoded, <c>LPUSH</c>ed onto <see cref="IngressRedis.MessageQueue"/> and
/// announced on <see cref="OutgressRedis.MessageDistributeChannel"/>.
/// </summary>
public class RedisEngineClientSender : EngineClientSender
{
    private readonly ISubscriber _redisPublisher;
    private readonly IConnectionMultiplexer _mux;

    private readonly IAltruistContext _context;

    RedisChannel channel = RedisChannel.Literal(OutgressRedis.MessageDistributeChannel);

    /// <summary>Creates the sender.</summary>
    /// <param name="store">Connection store used to decide local vs remote delivery.</param>
    /// <param name="codec">Encodes packets queued to Redis.</param>
    /// <param name="mux">Redis connection used for the list push and publish.</param>
    /// <param name="engine">Engine passed to <see cref="EngineClientSender"/>.</param>
    /// <param name="context">Supplies the process id stamped on queued packets.</param>
    public RedisEngineClientSender(IConnectionStore store, ICodec codec, IConnectionMultiplexer mux, IAltruistEngine engine, IAltruistContext context) : base(store, codec, engine)
    {
        _redisPublisher = mux.GetSubscriber();
        _mux = mux;
        _context = context;
    }

    /// <summary>
    /// Sends through <see cref="EngineClientSender"/> when <paramref name="clientId"/> has a connected socket here;
    /// otherwise queues an <see cref="InterprocessPacket"/> on Redis (network I/O).
    /// </summary>
    /// <param name="clientId">Target client id.</param>
    /// <param name="message">Packet to send.</param>
    public override async Task SendAsync<TPacket>(string clientId, TPacket message)
    {
        var socket = await _store.GetConnectionAsync(clientId);

        if (socket != null && socket.IsConnected)
        {
            await base.SendAsync(clientId, message);
        }
        else
        {
            var packet = new InterprocessPacket(_context.ProcessId, message);
            var redisMessage = _codec.Encoder.Encode(packet);
            await _mux.GetDatabase().ListLeftPushAsync(IngressRedis.MessageQueue, redisMessage);
            // just publishing an empty message this way we are notifying all subscribers that there are messages in the queue.
            await _redisPublisher.PublishAsync(channel, "", CommandFlags.FireAndForget);
        }
    }
}
