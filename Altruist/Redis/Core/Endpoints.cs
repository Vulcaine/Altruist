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

namespace Altruist.Redis;

/// <summary>
/// Redis names used on the receiving side of cross-process packet delivery
/// (see <see cref="RedisSocketClientSender"/> and <see cref="RedisEngineClientSender"/>).
/// </summary>
public static class IngressRedis
{
    /// <summary>Pub/sub channel on which an empty message signals that <see cref="MessageQueue"/> has new entries.</summary>
    public const string MessageDistributeChannel = "distribute-message";
    /// <summary>Redis list (un-prefixed key) that senders <c>LPUSH</c> encoded packets onto for clients not connected to this process.</summary>
    public const string MessageQueue = "message-queue";
}

/// <summary>Redis names used on the sending side of cross-process packet delivery.</summary>
public static class OutgressRedis
{
    /// <summary>Pub/sub channel senders publish to after queueing a packet; same value as <see cref="IngressRedis.MessageDistributeChannel"/>.</summary>
    public const string MessageDistributeChannel = "distribute-message";
}
