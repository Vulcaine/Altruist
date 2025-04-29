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

using System.Text.Json.Serialization;
using Altruist.Security;

namespace Altruist;

public interface IConnection : IStoredModel
{
    AuthDetails? AuthDetails { get; }
    string ConnectionId { get; }
    Task SendAsync(byte[] data);
    Task<byte[]> ReceiveAsync(CancellationToken cancellationToken);
    Task CloseAsync();

    bool Authenticated => AuthDetails != null && AuthDetails.IsAlive();

    bool IsConnected { get; }
}

public class Connection : StoredModel, IConnection
{
    [JsonIgnore] // Not serialized
    public AuthDetails? AuthDetails { get; set; }

    [JsonPropertyName("Type")]
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public override string Type { get => GetType().Name; set => Type = value; }

    [JsonPropertyName("ConnectionId")]
    public string ConnectionId { get; set; } = string.Empty;

    [JsonPropertyName("IsConnected")]
    public virtual bool IsConnected { get; set; }

    [JsonPropertyName("LastActivity")]
    public DateTime LastActivity { get; set; } = DateTime.UtcNow;
    public override string SysId { get; set; } = Guid.NewGuid().ToString();

    [JsonIgnore]
    string ITypedModel.Type { get => Type; set { /* Allow deserialization but ignore */ } }

    public virtual Task CloseOutputAsync()
    {
        throw new NotImplementedException();
    }

    public virtual Task CloseAsync()
    {
        throw new NotImplementedException();
    }

    public virtual Task<byte[]> ReceiveAsync(CancellationToken cancellationToken)
    {
        throw new NotImplementedException();
    }

    public virtual Task SendAsync(byte[] data)
    {
        throw new NotImplementedException();
    }
}

public interface ITransportClient
{
    Task ConnectAsync(string gatewayUrl);
    Task DisconnectAsync();
    Task SendAsync(byte[] data);
    Task<byte[]> ReceiveAsync(CancellationToken cancellationToken);
    bool IsConnected { get; }
}


public interface IConnectionManager
{
    Task HandleConnection(Connection socket, string @event, string clientId);
}

public interface IPortal : IConnectionManager
{
    void AddInterceptor(IInterceptor interceptor);
}
