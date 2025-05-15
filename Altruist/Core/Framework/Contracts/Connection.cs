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

public static class ConnectionStates
{
    public const int Connected = 0;
    public const int Joined = 1;
}

/// <summary>
/// Represents a persistent connection to a client within the game infrastructure.
/// Tracks connection status, activity, and provides core methods for sending and receiving data.
/// </summary>
public class Connection : StoredModel, IConnection
{
    /// <summary>
    /// Authentication-related metadata tied to this connection.
    /// Not serialized during transport.
    /// </summary>
    [JsonIgnore]
    public AuthDetails? AuthDetails { get; set; }

    /// <summary>
    /// Gets or sets the type name of the connection model.
    /// Used for serialization and type recognition.
    /// </summary>
    [JsonPropertyName("type")]
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public override string Type { get => GetType().Name; set => Type = value; }

    /// <summary>
    /// The unique identifier for this connection, typically tied to a transport-layer client ID.
    /// </summary>
    [JsonPropertyName("connectionId")]
    public string ConnectionId { get; set; } = string.Empty;

    /// <summary>
    /// Indicates whether the connection is currently active and responsive.
    /// </summary>
    [JsonPropertyName("isConnected")]
    public virtual bool IsConnected { get; set; }

    /// <summary>
    /// Timestamp of the last activity (message sent or received) on this connection.
    /// Useful for detecting timeouts or inactive clients.
    /// </summary>
    [JsonPropertyName("lastActivity")]
    public DateTime LastActivity { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// The logical state of the connection used to manage routing/broadcast behavior.
    /// <para>0 = connected (default)</para>
    /// <para>1 = player joined a game</para>
    /// Additional states can be defined as needed to support session routing and broadcast filtering.
    /// </summary>
    [JsonPropertyName("connectionState")]
    public int ConnectionState { get; set; } = 0;

    /// <summary>
    /// System identifier used for persistence and deduplication.
    /// </summary>
    public override string SysId { get; set; } = Guid.NewGuid().ToString();

    /// <summary>
    /// Internal implementation for ITypedModel to support polymorphic deserialization.
    /// The setter is intentionally ignored at runtime.
    /// </summary>
    [JsonIgnore]
    string ITypedModel.Type { get => Type; set { /* Allow deserialization but ignore */ } }

    /// <summary>
    /// Gracefully closes the output stream of the connection (if supported).
    /// Override in derived types to provide protocol-specific teardown.
    /// </summary>
    public virtual Task CloseOutputAsync()
    {
        throw new NotImplementedException();
    }

    /// <summary>
    /// Closes the entire connection, releasing any associated resources.
    /// </summary>
    public virtual Task CloseAsync()
    {
        throw new NotImplementedException();
    }

    /// <summary>
    /// Receives raw byte data from the connection.
    /// Blocks until data is received or the cancellation token is triggered.
    /// </summary>
    /// <param name="cancellationToken">Token to cancel the receive operation.</param>
    /// <returns>Byte array of received data.</returns>
    public virtual Task<byte[]> ReceiveAsync(CancellationToken cancellationToken)
    {
        throw new NotImplementedException();
    }

    /// <summary>
    /// Sends raw byte data to the connected client.
    /// </summary>
    /// <param name="data">The byte payload to send.</param>
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
