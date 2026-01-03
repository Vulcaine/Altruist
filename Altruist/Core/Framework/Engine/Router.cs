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
using System.Security.Cryptography;

using MessagePack;

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
    private readonly ConcurrentDictionary<string, byte> _inFlight = new();

    public EngineClientSender(IConnectionStore store, ICodec codec, IAltruistEngine engine) : base(store, codec)
    {
        _engine = engine;
    }

    public override Task SendAsync<TPacketBase>(string clientId, TPacketBase message)
    {
        if (message == null)
            return Task.CompletedTask;

        var type = message.GetType();
        var hash = ComputeContentHash(type, message);
        var key = $"{clientId}:{type.Name}:{hash:x16}";
        if (!_inFlight.TryAdd(key, 0))
            return Task.CompletedTask;

        var identifier = new TaskIdentifier(key);

        _engine.SendTask(identifier, async () =>
        {
            try
            {
                await base.SendAsync(clientId, message).ConfigureAwait(false);
            }
            finally
            {
                _inFlight.TryRemove(key, out _);
            }
        });

        return Task.CompletedTask;
    }

    private static ulong ComputeContentHash(Type type, object message)
    {
        byte[] bytes = MessagePackSerializer.Serialize(type, message);
        var sha = SHA256.HashData(bytes);
        return BitConverter.ToUInt64(sha, 0);
    }
}

[Service(typeof(IAltruistEngineRouter))]
[ConditionalOnConfig("altruist:game:engine")]
public class InMemoryEngineRouter : EngineRouter
{
    public InMemoryEngineRouter(IConnectionStore store, ICodec codec, EngineClientSender clientSender, RoomSender roomSender, BroadcastSender broadcastSender, IClientSynchronizator clientSynchronizator, IAltruistEngine engine) : base(store, codec, clientSender, roomSender, broadcastSender, clientSynchronizator, engine)
    {
    }
}
