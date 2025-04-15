using System.Text.Json.Serialization;
using Altruist.Authentication;

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
    public override string GenId { get => throw new NotImplementedException(); set => throw new NotImplementedException(); }

    [JsonIgnore]
    string ITypedModel.Type { get => Type; set { /* Allow deserialization but ignore */ } }

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
