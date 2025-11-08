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

public abstract class PortalContext : IPortalContext
{
    public virtual IAltruistRouter Router { get; }
    public ICodec Codec { get; }

    public IAltruistContext AltruistContext { get; protected set; }

    public IServiceProvider ServiceProvider { get; protected set; }

    public PortalContext(IAltruistContext altruistContext, IServiceProvider provider)
    {
        AltruistContext = altruistContext;
        Codec = provider.GetService<ICodec>() ?? new JsonCodec();
        Router = provider.GetRequiredService<IAltruistRouter>();
        Codec = provider.GetService<ICodec>() ?? new JsonCodec();
        ServiceProvider = provider;
    }

    public virtual void Initialize()
    {

    }
}

public abstract class Portal : IPortal
{
    // protected readonly IPortalContext _context;
    // protected readonly ILogger Logger;

    // protected IAltruistRouter Router => _context.Router;
    // private ICodec _codec => _context.Codec;

    // protected ISocketManager SocketManager;

    // private readonly List<IInterceptor> _interceptors = new();

    public Portal()
    {
        // _context = context;
        // SocketManager = context.ServiceProvider.GetService<ISocketManager>()!;
        // Logger = loggerFactory.CreateLogger(GetType());
    }

    // public void AddInterceptor(IInterceptor interceptor) => _interceptors.Add(interceptor);

    // public virtual async Task OnDisconnectedAsync(string clientId, Exception? exception = null)
    // {
    //     await Cleanup();
    // }

    // public virtual Task OnConnectedAsync(string clientId)
    // {
    //     return Task.CompletedTask;
    // }

    // protected async Task<bool> ProcessPacket(AltruistPacket packet, byte[] bytes, string @event, string clientId)
    // {
    //     if (string.IsNullOrEmpty(packet.Event)) return false;

    //     if (EventHandlerRegistry<IPortal>.TryGetHandler(packet.Event, out var @delegate))
    //     {
    //         var data = bytes;
    //         var context = new InterceptContext(@event);
    //         var handlerMethod = @delegate.Method;
    //         var parameterType = handlerMethod.GetParameters()[0].ParameterType;

    //         IPacket? message = _codec.Decoder.Decode<IPacket>(data, parameterType);

    //         var interceptorTasks = _interceptors.Select(interceptor => interceptor.Intercept(context, message));
    //         var interceptorExecution = Task.WhenAll(interceptorTasks);

    //         Task? handlerTask = data != null ? (Task?)@delegate.DynamicInvoke(message, clientId) : null;

    //         if (handlerTask != null)
    //         {
    //             await handlerTask;
    //         }

    //         await interceptorExecution;
    //     }
    //     else
    //     {
    //         Logger.LogWarning($"No handler found for event: {packet.Event}");
    //     }

    //     return true;
    // }

    // public async Task HandleConnection(Connection connection, string @event, string clientId)
    // {
    //     await AddConnectionAsync(clientId, connection);

    //     try
    //     {
    //         while (true)
    //         {
    //             var packetData = await connection.ReceiveAsync(CancellationToken.None);
    //             if (packetData.Length == 0)
    //             {
    //                 break;
    //             }

    //             var packet = _codec.Decoder.Decode<AltruistPacket>(packetData);
    //             if (!await ProcessPacket(packet, packetData, @event, clientId)) break;
    //         }
    //     }
    //     catch (Exception ex)
    //     {
    //         Logger.LogError($"Error handling connection: {ex}");
    //         await OnDisconnectedAsync(clientId, ex);
    //     }
    //     finally
    //     {
    //         await OnDisconnectedAsync(clientId);
    //         await RemoveConnectionAsync(clientId);
    //         await Cleanup();
    //     }
    // }

    // public Task RemoveConnectionAsync(string connectionId)
    // {
    //     return SocketManager.RemoveConnectionAsync(connectionId);
    // }

    // public Task<bool> AddConnectionAsync(string connectionId, Connection socket, string? roomId = null)
    // {
    //     return SocketManager.AddConnectionAsync(connectionId, socket, roomId);
    // }

    // public Task<Connection?> GetConnectionAsync(string connectionId)
    // {
    //     return SocketManager.GetConnectionAsync(connectionId);
    // }

    // public Task<IEnumerable<string>> GetAllConnectionIdsAsync()
    // {
    //     return SocketManager.GetAllConnectionIdsAsync();
    // }

    // public virtual async Task<Dictionary<string, Connection>> GetAllConnectionsDictAsync()
    // {
    //     return await SocketManager.GetAllConnectionsDictAsync();
    // }

    // public Task<ICursor<Connection>> GetAllConnectionsAsync()
    // {
    //     return SocketManager.GetAllConnectionsAsync();
    // }

    // private async Task<TPacketBase> ReceiveAsync<TPacketBase>(string clientId) where TPacketBase : IPacketBase
    // {
    //     var connections = await GetAllConnectionsDictAsync();

    //     if (connections.TryGetValue(clientId, out var connection))
    //     {
    //         var data = await connection.ReceiveAsync(CancellationToken.None);
    //         return _codec.Decoder.Decode<TPacketBase>(data);
    //     }

    //     return default!;
    // }



}
