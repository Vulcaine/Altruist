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

public interface IAltruistEngineRouter : IAltruistRouter { }

[Service(typeof(IAltruistEngineRouter))]
[Service(typeof(IAltruistRouter))]
[ConditionalOnConfig("altruist:game:engine")]
public abstract class EngineRouter : AbstractAltruistRouter, IAltruistEngineRouter
{
    private readonly IAltruistEngine _engine;

    protected EngineRouter(IConnectionStore store, ICodec codec, EngineClientSender clientSender, RoomSender roomSender, BroadcastSender broadcastSender, IClientSynchronizator clientSynchronizator, IAltruistEngine engine) : base(store, codec, clientSender, roomSender, broadcastSender, clientSynchronizator)
    {
        _engine = engine;
    }

    public virtual void SendTask(TaskIdentifier taskIdentifier, Delegate task)
    {
        _engine.SendTask(taskIdentifier, task);
    }
}

[Service]
[ConditionalOnConfig("altruist:game:engine")]
public class EngineClientSender : ClientSender
{
    private readonly IAltruistEngine _engine;

    public EngineClientSender(IConnectionStore store, ICodec codec, IAltruistEngine engine, IDashboardNetworkRecorder? networkRecorder = null) : base(store, codec, networkRecorder)
    {
        _engine = engine;
    }

    private static long _sendCounter;

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

[Service(typeof(IAltruistEngineRouter))]
[ConditionalOnConfig("altruist:game:engine")]
public class InMemoryEngineRouter : EngineRouter
{
    public InMemoryEngineRouter(IConnectionStore store, ICodec codec, EngineClientSender clientSender, RoomSender roomSender, BroadcastSender broadcastSender, IClientSynchronizator clientSynchronizator, IAltruistEngine engine) : base(store, codec, clientSender, roomSender, broadcastSender, clientSynchronizator, engine)
    {
    }
}
