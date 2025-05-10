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

using System.Collections.Concurrent;

namespace Altruist.Engine;

public interface IAltruistEngineRouter : IAltruistRouter { }

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


public class EngineClientSender : ClientSender
{
    private readonly IAltruistEngine _engine;
    public EngineClientSender(IConnectionStore store, ICodec codec, IAltruistEngine engine) : base(store, codec)
    {
        _engine = engine;
    }

    public override Task SendAsync<TPacketBase>(string clientId, TPacketBase message)
    {
        // Efficient string allocation using string.Create (avoids clientId + message.Type allocation)
        var id = string.Create(clientId.Length + 1 + message.Type.Length, (clientId, message.Type), (span, state) =>
        {
            state.clientId.AsSpan().CopyTo(span);
            span[state.clientId.Length] = ':';
            state.Item2.AsSpan().CopyTo(span.Slice(state.clientId.Length + 1));
        });

        var identifier = new TaskIdentifier(id);
        _engine.SendTask(identifier, () => base.SendAsync(clientId, message));
        return Task.CompletedTask;
    }
}


public class InMemoryEngineRouter : EngineRouter
{
    public InMemoryEngineRouter(IConnectionStore store, ICodec codec, EngineClientSender clientSender, RoomSender roomSender, BroadcastSender broadcastSender, IClientSynchronizator clientSynchronizator, IAltruistEngine engine) : base(store, codec, clientSender, roomSender, broadcastSender, clientSynchronizator, engine)
    {
    }
}
