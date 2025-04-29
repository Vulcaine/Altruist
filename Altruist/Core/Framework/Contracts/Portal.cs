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

using Altruist.Codec;
using Microsoft.Extensions.DependencyInjection;

namespace Altruist;

public interface IPortalContext : IConnectionStore
{
    ICacheProvider Cache { get; }
    IAltruistRouter Router { get; }
    ICodec Codec { get; }

    IAltruistContext AltruistContext { get; }
    IServiceProvider ServiceProvider { get; }

    void Initialize();
}


public abstract class AbstractSocketPortalContext : IPortalContext
{
    protected readonly IConnectionStore _connectionStore;
    public virtual IAltruistRouter Router { get; }
    public ICodec Codec { get; }

    public IAltruistContext AltruistContext { get; protected set; }
    public IServiceProvider ServiceProvider { get; }

    public ICacheProvider Cache { get; }

    public AbstractSocketPortalContext(
        IAltruistContext altruistContext, IServiceProvider serviceProvider)
    {
        AltruistContext = altruistContext;
        Codec = serviceProvider.GetService<ICodec>() ?? new JsonCodec();
        _connectionStore = serviceProvider.GetRequiredService<IConnectionStore>();
        Router = serviceProvider.GetRequiredService<IAltruistRouter>();
        ServiceProvider = serviceProvider;
        Cache = serviceProvider.GetRequiredService<ICacheProvider>();
    }

    public abstract void Initialize();

    public virtual async Task<Dictionary<string, Connection>> GetConnectionsInRoomAsync(string roomId)
    {
        return await _connectionStore.GetConnectionsInRoomAsync(roomId);
    }

    public virtual async Task<RoomPacket> FindAvailableRoomAsync()
    {
        return await _connectionStore.FindAvailableRoomAsync();
    }

    public virtual async Task<RoomPacket> CreateRoomAsync()
    {
        return await _connectionStore.CreateRoomAsync();
    }

    public virtual Task DeleteRoomAsync(string roomName)
    {
        return _connectionStore.DeleteRoomAsync(roomName);
    }

    public virtual Task RemoveConnectionAsync(string connectionId)
    {
        return _connectionStore.RemoveConnectionAsync(connectionId);
    }

    public virtual Task<bool> AddConnectionAsync(string connectionId, Connection socket, string? roomId = null)
    {
        return _connectionStore.AddConnectionAsync(connectionId, socket, roomId);
    }

    public virtual Task<Connection?> GetConnectionAsync(string connectionId)
    {
        return _connectionStore.GetConnectionAsync(connectionId);
    }

    public virtual Task<IEnumerable<string>> GetAllConnectionIdsAsync()
    {
        return _connectionStore.GetAllConnectionIdsAsync();
    }

    public virtual Task<ICursor<Connection>> GetAllConnectionsAsync()
    {
        return _connectionStore.GetAllConnectionsAsync();
    }

    public virtual async Task<RoomPacket?> FindRoomForClientAsync(string clientId)
    {
        return await _connectionStore.FindRoomForClientAsync(clientId);
    }

    public virtual async Task<RoomPacket?> GetRoomAsync(string roomId)
    {
        return await _connectionStore.GetRoomAsync(roomId);
    }
    public virtual async Task<Dictionary<string, RoomPacket>> GetAllRoomsAsync()
    {
        return await _connectionStore.GetAllRoomsAsync();
    }

    public virtual async Task<RoomPacket?> AddClientToRoomAsync(string connectionId, string roomId)
    {
        return await _connectionStore.AddClientToRoomAsync(connectionId, roomId);
    }

    public virtual async Task SaveRoomAsync(RoomPacket room)
    {
        await _connectionStore.SaveRoomAsync(room);
    }

    public virtual async Task Cleanup()
    {
        await _connectionStore.Cleanup();
    }

    public virtual async Task<bool> IsConnectionExistsAsync(string connectionId)
    {
        return await _connectionStore.IsConnectionExistsAsync(connectionId);
    }

    public virtual async Task<Dictionary<string, Connection>> GetAllConnectionsDictAsync()
    {
        return await _connectionStore.GetAllConnectionsDictAsync();
    }
}