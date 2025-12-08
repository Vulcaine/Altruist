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

public interface IAltruistConnection : IStoredModel
{
    AuthDetails? AuthDetails { get; }
    string ConnectionId { get; }
    Task SendAsync(byte[] data);
    Task<byte[]> ReceiveAsync(CancellationToken cancellationToken);
    Task CloseAsync();

    bool Authenticated => AuthDetails != null && AuthDetails.IsAlive();

    bool IsConnected { get; }
}

public class AltruistConnection : StoredModel, IAltruistConnection
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
    public override string StorageId { get; set; } = Guid.NewGuid().ToString();

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
    Task HandleConnection(AltruistConnection socket, string @event, string clientId);
    Task<bool> ProcessPacket(AltruistPacket packet, byte[] bytes, string @event, string clientId);
    void AddInterceptor(IInterceptor interceptor);
    Task RemoveConnectionAsync(string connectionId);
    Task<bool> AddConnectionAsync(string connectionId, AltruistConnection socket, string? roomId = null);
    Task<AltruistConnection?> GetConnectionAsync(string connectionId);
    Task<IEnumerable<string>> GetAllConnectionIdsAsync();
    Task DisconnectEngineAwareAsync(string clientId);
    Task DisconnectAsync(string clientId);
    Task<Dictionary<string, AltruistConnection>> GetAllConnectionsDictAsync();
    Task<ICursor<AltruistConnection>> GetAllConnectionsAsync();
    Task<Dictionary<string, AltruistConnection>> GetConnectionsInRoomAsync(string roomId);
    Task<RoomPacket> FindAvailableRoomAsync();
    Task<RoomPacket?> FindRoomForClientAsync(string clientId);
    Task<RoomPacket> CreateRoomAsync();
    Task DeleteRoomAsync(string roomName);
    Task<RoomPacket?> GetRoomAsync(string roomId);
    Task<Dictionary<string, RoomPacket>> GetAllRoomsAsync();
    Task<RoomPacket?> JoinRoomAsync(string connectionId, string roomId);
    Task SaveRoomAsync(RoomPacket room);
    Task Cleanup();
    Task<bool> IsConnectionExistsAsync(string connectionId);
}

