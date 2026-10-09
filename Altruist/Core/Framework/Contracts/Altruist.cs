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

using Altruist.Contracts;
using Altruist.Engine;

namespace Altruist;

/// <summary>
/// The server's readiness state machine. Readiness is <see cref="ReadyState.Alive"/> only when every tracked
/// <see cref="IConnectable"/> is connected; the game engine is started and stopped with it.
/// </summary>
/// <remarks>Singleton (<c>ServerStatus</c>); read <see cref="Status"/> to gate work on readiness. Health endpoints expose it
/// at <c>GET /altruist/health</c>.</remarks>
public interface IServerStatus
{
    /// <summary>Current readiness: <see cref="ReadyState.Starting"/>, <see cref="ReadyState.Alive"/> or <see cref="ReadyState.Failed"/>.</summary>
    ReadyState Status { get; }
    /// <summary>Sets the state and starts (<see cref="ReadyState.Alive"/>) or stops (<see cref="ReadyState.Failed"/>) the engine.
    /// Normally driven by the framework from connection events; call it only to force a state.</summary>
    /// <param name="engine">The engine to start or stop (may be null in the implementation).</param>
    /// <param name="state">New state.</param>
    /// <param name="token">Passed to the engine when it starts.</param>
    void SignalState(IEngineCore engine, ReadyState state, CancellationToken token);
}


/// <summary>
/// Process-wide description of the running server: address, registered endpoints, active backend tokens and process id.
/// </summary>
/// <remarks>Singleton (<c>AltruistServerContext</c>). Populated by the framework during startup (portals add their
/// endpoints); inject it to read server info, e.g. for diagnostics or service discovery.</remarks>
public interface IAltruistContext
{
    // IServerStatus AppStatus { get; set; }
    /// <summary>The active transport backend, or null when none is set up.</summary>
    ITransportServiceToken? TransportToken { get; set; }
    /// <summary>Active database backends (may be empty).</summary>
    List<IDatabaseServiceToken> DatabaseTokens { get; set; }
    /// <summary>The active cache backend, or null.</summary>
    ICacheServiceToken? CacheToken { get; set; }
    /// <summary>Public name, protocol, host and port of this server.</summary>
    ServerInfo ServerInfo { get; set; }
    /// <summary>Registered endpoint paths (portal routes and others).</summary>
    HashSet<string> Endpoints { get; set; }
    /// <summary>Unique id of this process (machine name, OS pid and a random suffix); differs on every start.</summary>
    public string ProcessId { get; }
    /// <summary>Whether the game engine is configured for this process.</summary>
    bool EngineEnabled { get; set; }
    /// <summary>Adds an endpoint path to <see cref="Endpoints"/> (duplicates are ignored).</summary>
    /// <param name="endpoint">Endpoint path, e.g. a portal route.</param>
    void AddEndpoint(string endpoint);
    /// <summary>Checks that the context describes a servable setup.</summary>
    /// <exception cref="ArgumentException">No endpoints are registered or no transport is set up (default implementation).</exception>
    void Validate();
}
