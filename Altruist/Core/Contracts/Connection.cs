using System.Text.Json.Serialization;
using Altruist.Authentication;
using Redis.OM.Modeling;

namespace Altruist;

public interface IConnection : IModel
{
    AuthDetails? AuthDetails { get; }
    string ConnectionId { get; }
    Task SendAsync(byte[] data);
    Task<byte[]> ReceiveAsync(CancellationToken cancellationToken);
    Task CloseAsync();

    bool Authenticated => AuthDetails != null && AuthDetails.IsAlive();

    bool IsConnected { get; }
}

[Document(StorageType = StorageType.Json, IndexName = "connections", Prefixes = new[] { "connections" })]
public class Connection : IConnection
{
    [JsonIgnore] // Not serialized
    public AuthDetails? AuthDetails { get; set; }

    [JsonPropertyName("Type")]
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public string Type => GetType().Name;

    [Indexed]
    [JsonPropertyName("ConnectionId")]
    public string ConnectionId { get; set; } = string.Empty;

    [JsonPropertyName("IsConnected")]
    public bool IsConnected { get; set; }

    [JsonPropertyName("LastActivity")]
    public DateTime LastActivity { get; set; } = DateTime.UtcNow;


    [JsonIgnore]
    string IModel.Type { get => Type; set { /* Allow deserialization but ignore */ } }

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

}
