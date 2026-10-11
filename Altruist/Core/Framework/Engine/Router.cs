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

namespace Altruist.Engine;

/// <summary>
/// The engine-aware <see cref="IAltruistRouter"/>, registered when <c>altruist:game:engine</c> is configured. Same send
/// API; its client sender defers sync packets to the engine loop. Inject <see cref="IAltruistRouter"/> unless you
/// specifically need the engine variant.
/// </summary>
public interface IAltruistEngineRouter : IAltruistRouter { }

/// <summary>
/// Base router used when the game engine is configured. Uses <see cref="EngineClientSender"/>, so
/// <see cref="PacketCodes.Sync"/> packets sent to a client are delivered from the engine loop rather than inline.
/// </summary>
[Service(typeof(IAltruistEngineRouter))]
[Service(typeof(IAltruistRouter))]
[ConditionalOnConfig("altruist:game:engine")]
public abstract class EngineRouter : AbstractAltruistRouter, IAltruistEngineRouter
{
    private readonly IAltruistEngine _engine;

    /// <summary>Creates the router.</summary>
    /// <param name="store">The connection store.</param>
    /// <param name="codec">The packet codec.</param>
    /// <param name="clientSender">The engine-aware client sender.</param>
    /// <param name="roomSender">Sender for rooms.</param>
    /// <param name="broadcastSender">Sender for all clients.</param>
    /// <param name="clientSynchronizator">Entity delta-sync sender.</param>
    /// <param name="engine">The engine tasks are sent to.</param>
    protected EngineRouter(IConnectionStore store, ICodec codec, EngineClientSender clientSender, RoomSender roomSender, BroadcastSender broadcastSender, IClientSynchronizator clientSynchronizator, IAltruistEngine engine) : base(store, codec, clientSender, roomSender, broadcastSender, clientSynchronizator)
    {
        _engine = engine;
    }

    /// <summary>Forwards to <see cref="IEngineCore.SendTask"/>: requests one run of <paramref name="task"/> on the engine loop, coalesced per id.</summary>
    /// <param name="taskIdentifier">The coalescing key.</param>
    /// <param name="task">A parameterless delegate.</param>
    public virtual void SendTask(TaskIdentifier taskIdentifier, Delegate task)
    {
        _engine.SendTask(taskIdentifier, task);
    }
}

/// <summary>
/// <see cref="ClientSender"/> used with the game engine: packets whose <c>MessageCode</c> is <see cref="PacketCodes.Sync"/>
/// are sent from an engine task (<see cref="IEngineCore.SendTask"/>, one unique task per send) and the call returns
/// immediately; every other packet is sent exactly like <see cref="ClientSender"/>. Registered as a singleton when
/// <c>altruist:game:engine</c> is configured.
/// </summary>
/// <remarks>For deferred sync packets the returned task completes before the send happens, so send errors are not
/// observed by the caller (the engine logs them).</remarks>
[Service]
[ConditionalOnConfig("altruist:game:engine")]
public class EngineClientSender : ClientSender
{
    private readonly IAltruistEngine _engine;

    /// <summary>Creates a sender without shared outbound queues (for manual construction and tests).</summary>
    /// <param name="store">The connection store.</param>
    /// <param name="codec">The packet codec.</param>
    /// <param name="engine">The engine sync sends are scheduled on.</param>
    /// <param name="networkRecorder">Optional dashboard recorder.</param>
    public EngineClientSender(IConnectionStore store, ICodec codec, IAltruistEngine engine, IDashboardNetworkRecorder? networkRecorder = null)
        : this(store, codec, engine, outbound: null, networkRecorder)
    {
    }

    /// <summary>The constructor DI uses: the process-wide <see cref="OutboundQueues"/> (shared by every sender).</summary>
    public EngineClientSender(IConnectionStore store, ICodec codec, IAltruistEngine engine, OutboundQueues? outbound,
        IDashboardNetworkRecorder? networkRecorder = null) : base(store, codec, outbound, networkRecorder)
    {
        _engine = engine;
    }

    private static long _sendCounter;

    /// <summary>
    /// Sends a packet to one client; <see cref="PacketCodes.Sync"/> packets are scheduled on the engine and the method
    /// returns at once, others go through <see cref="ClientSender.SendAsync{TPacketBase}(string, TPacketBase)"/>. A
    /// <c>null</c> packet is ignored.
    /// </summary>
    /// <typeparam name="TPacketBase">The packet type.</typeparam>
    /// <param name="clientId">The connection id.</param>
    /// <param name="message">The packet to send.</param>
    public override Task SendAsync<TPacketBase>(string clientId, TPacketBase message)
    {
        if (message == null)
            return Task.CompletedTask;

        if (!ShouldRunThroughEngine(message))
            return base.SendAsync(clientId, message);

        var id = Interlocked.Increment(ref _sendCounter);
        var key = $"send:{id}";
        var identifier = new TaskIdentifier(key);

        _engine.SendTask(identifier, async () =>
        {
            await base.SendAsync(clientId, message).ConfigureAwait(false);
        });

        return Task.CompletedTask;
    }

    private static bool ShouldRunThroughEngine<TPacketBase>(TPacketBase message) where TPacketBase : IPacketBase
        => message.MessageCode == Altruist.PacketCodes.Sync;
}

/// <summary>The default <see cref="IAltruistEngineRouter"/> (single process). Inject <see cref="IAltruistRouter"/> rather than this type.</summary>
[Service(typeof(IAltruistEngineRouter))]
[ConditionalOnConfig("altruist:game:engine")]
public class InMemoryEngineRouter : EngineRouter
{
    /// <summary>Creates the router.</summary>
    /// <param name="store">The connection store.</param>
    /// <param name="codec">The packet codec.</param>
    /// <param name="clientSender">The engine-aware client sender.</param>
    /// <param name="roomSender">Sender for rooms.</param>
    /// <param name="broadcastSender">Sender for all clients.</param>
    /// <param name="clientSynchronizator">Entity delta-sync sender.</param>
    /// <param name="engine">The engine tasks are sent to.</param>
    public InMemoryEngineRouter(IConnectionStore store, ICodec codec, EngineClientSender clientSender, RoomSender roomSender, BroadcastSender broadcastSender, IClientSynchronizator clientSynchronizator, IAltruistEngine engine) : base(store, codec, clientSender, roomSender, broadcastSender, clientSynchronizator, engine)
    {
    }
}
