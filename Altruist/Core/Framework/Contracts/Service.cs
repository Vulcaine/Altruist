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

namespace Altruist;

public interface IService
{
    public string ServiceName { get; }
}

public interface IConnectable : IService
{
    bool IsConnected { get; }
    event Action? OnConnected;
    event Action<Exception> OnFailed;
    event Action<Exception> OnRetryExhausted;

    Task ConnectAsync(
        string protocol, string host, int port,
        int maxRetries = 30, int delayMilliseconds = 2000);

    Task ConnectAsync();

    void RaiseOnRetryExhaustedEvent(Exception ex);
    void RaiseFailedEvent(Exception ex);
    void RaiseConnectedEvent();
}

public interface IRelayService : IConnectable
{
    Task Relay(IPacket data);
    Task Relay(byte[] message);
}

// public abstract class AbstractRelayService : IRelayService
// {
//     public abstract string RelayEvent { get; }

//     public abstract string ServiceName { get; }
//     public abstract bool IsConnected { get; }

//     public event Action? OnConnected;
//     public event Action<Exception> OnRetryExhausted = _ => { };
//     public event Action<Exception> OnFailed = _ => { };

//     public abstract Task ConnectAsync(int maxRetries = 30, int delayMilliseconds = 2000);
//     public abstract Task Relay(IPacket data);

//     public void RaiseConnectedEvent()
//     {
//         OnConnected?.Invoke();
//     }

//     public void RaiseFailedEvent(Exception ex)
//     {
//         OnFailed?.Invoke(ex);
//     }

//     public void RaiseOnRetryExhaustedEvent(Exception ex)
//     {
//         OnRetryExhausted?.Invoke(ex);
//     }
// }