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

namespace Altruist.InMemory;

/// <summary>
/// <see cref="IAltruistRouter"/> for single-instance deployments: sends client, room and broadcast messages directly
/// to connections in the local connection store. Registered when <c>altruist:persistence:cache:provider</c> is
/// <c>inmemory</c>; inject <see cref="IAltruistRouter"/> rather than this type.
/// </summary>
[Service(typeof(IAltruistRouter))]
[ConditionalOnConfig("altruist:persistence:cache:provider", havingValue: "inmemory")]
public class InMemoryDirectRouter : DirectRouter
{
    /// <summary>Created by DI.</summary>
    /// <param name="store">Connection store.</param>
    /// <param name="codec">Message codec.</param>
    /// <param name="clientSender">Sender for single clients.</param>
    /// <param name="roomSender">Sender for rooms.</param>
    /// <param name="broadcastSender">Sender for broadcasts.</param>
    /// <param name="clientSynchronizator">Client state synchronizer.</param>
    public InMemoryDirectRouter(IConnectionStore store, ICodec codec, ClientSender clientSender, RoomSender roomSender, BroadcastSender broadcastSender, IClientSynchronizator clientSynchronizator) : base(store, codec, clientSender, roomSender, broadcastSender, clientSynchronizator)
    {
    }
}
