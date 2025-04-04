using System.Text;
using Altruist;
using Altruist.Transport;
using Newtonsoft.Json;

namespace Altruist.Web;

// public class AltruistRelayService : IRelayService
// {
//     private readonly string _gatewayUrl;
//     private readonly string _eventName;
//     private readonly TimeSpan _reconnectDelay = TimeSpan.FromSeconds(5);
//     private ITransportClient? _transportClient;
//     private CancellationTokenSource _cts = new();

//     private RelayPortal _socketPortal;

//     public string RelayEvent => _eventName;

//     public AltruistRelayService(string gatewayUrl, string eventName, RelayPortal socketPortal)
//     {
//         _gatewayUrl = gatewayUrl;
//         _eventName = eventName;
//         _socketPortal = socketPortal;
//     }

//     public void SetTransportClient(ITransportClient transportClient)
//     {
//         _transportClient = transportClient;
//     }

//     public async Task Relay(Message data)
//     {
//         if (_transportClient != null && _transportClient.IsConnected)
//         {
//             var json = JsonConvert.SerializeObject(data);
//             var bytes = Encoding.UTF8.GetBytes(json);
//             await _transportClient.SendAsync(bytes);
//         }
//     }

//     public async Task ConnectAsync()
//     {
//         if (_transportClient == null)
//             throw new InvalidOperationException("Transport client not set.");

//         while (true)
//         {
//             try
//             {
//                 Console.WriteLine($"Connecting to {_gatewayUrl}...");
//                 await _transportClient.ConnectAsync();
//                 Console.WriteLine($"Connected to {_gatewayUrl}.");

//                 _ = ListenForMessages();
//                 break;
//             }
//             catch (Exception ex)
//             {
//                 Console.WriteLine($"Failed to connect: {ex.Message}. Retrying in {_reconnectDelay.Seconds} seconds...");
//                 await Task.Delay(_reconnectDelay);
//             }
//         }
//     }

//     private async Task ListenForMessages()
//     {
//         while (_transportClient != null && _transportClient.IsConnected)
//         {
//             try
//             {
//                 var message = await _transportClient.ReceiveAsync(_cts.Token);
//                 await _socketPortal.Relay(message);
//             }
//             catch (Exception ex)
//             {
//                 Console.WriteLine($"Error receiving message: {ex.Message}");
//                 await Task.Delay(_reconnectDelay);  // Retry after delay
//             }
//         }
//     }

//     public async Task DisconnectAsync()
//     {
//         if (_transportClient != null)
//         {
//             await _transportClient.DisconnectAsync();
//         }
//     }
// }
