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

/// <summary>A named framework service; the name identifies it in logs and health output.</summary>
public interface IService
{
    /// <summary>Human-readable service name (shown in logs and <c>GET /altruist/health/details</c>).</summary>
    public string ServiceName { get; }
}

/// <summary>
/// A service backed by an external connection (database, cache, broker) whose availability gates server readiness.
/// </summary>
/// <remarks>
/// Every <see cref="IConnectable"/> registered in DI (plus the active database and cache providers) is tracked by the
/// server status: at startup it calls the parameterless <see cref="ConnectAsync()"/> on each one that is not yet
/// connected, turns <see cref="ReadyState.Alive"/> once all have raised <see cref="OnConnected"/>, drops to
/// <see cref="ReadyState.Failed"/> (engine stopped) on <see cref="OnFailed"/>, and shuts the application down on
/// <see cref="OnRetryExhausted"/>. Implementations must raise these events themselves via the <c>Raise*</c> methods,
/// including on reconnect.
/// </remarks>
public interface IConnectable : IService
{
    /// <summary>Whether the connection is currently up.</summary>
    bool IsConnected { get; }
    /// <summary>Raised when the connection is (re)established.</summary>
    event Action? OnConnected;
    /// <summary>Raised when an established connection is lost; the server goes to <see cref="ReadyState.Failed"/> until reconnected.</summary>
    event Action<Exception> OnFailed;
    /// <summary>Raised when connecting gave up after all retries; the server shuts down.</summary>
    event Action<Exception> OnRetryExhausted;

    /// <summary>Connects to an explicit endpoint, retrying on failure.</summary>
    /// <param name="protocol">Scheme or protocol name expected by the implementation.</param>
    /// <param name="host">Host name or address.</param>
    /// <param name="port">Port.</param>
    /// <param name="maxRetries">Attempts before raising <see cref="OnRetryExhausted"/>.</param>
    /// <param name="delayMilliseconds">Delay between attempts, in milliseconds.</param>
    Task ConnectAsync(
        string protocol, string host, int port,
        int maxRetries = 30, int delayMilliseconds = 2000);

    /// <summary>Connects using the implementation's configured endpoint. This is the overload the framework calls at startup.</summary>
    Task ConnectAsync();

    /// <summary>Raises <see cref="OnRetryExhausted"/>.</summary>
    /// <param name="ex">The last connection error.</param>
    void RaiseOnRetryExhaustedEvent(Exception ex);
    /// <summary>Raises <see cref="OnFailed"/>.</summary>
    /// <param name="ex">The connection error.</param>
    void RaiseFailedEvent(Exception ex);
    /// <summary>Raises <see cref="OnConnected"/>.</summary>
    void RaiseConnectedEvent();
}

/// <summary>
/// Forwards packets to an external system (another server or broker). Used by the relay interceptor, which passes every
/// decoded inbound packet to <see cref="Relay(IPacket)"/>. No built-in implementation is registered; provide one with
/// <c>[Service(typeof(IRelayService))]</c> if you use relaying.
/// </summary>
public interface IRelayService : IConnectable
{
    /// <summary>Forwards a decoded packet.</summary>
    /// <param name="data">The packet.</param>
    Task Relay(IPacket data);
    /// <summary>Forwards already-encoded bytes.</summary>
    /// <param name="message">The encoded packet.</param>
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
