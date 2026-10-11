/*
Copyright 2025 Aron Gere
Licensed under the Apache License, Version 2.0
*/

using System.Diagnostics;
using System.Reflection;

using Altruist.Transport;
using Altruist.UORM;

using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

namespace Altruist.Dashboard;

/// <summary>Takes this server's <see cref="ArchitectureCardDto"/>: its listeners, portals and stores, as running now.</summary>
[Service]
[ConditionalOnConfig("altruist:dashboard:enabled", havingValue: "true")]
[ConditionalOnAssembly("Altruist.Dashboard")]
public sealed class ArchitectureCardBuilder
{
    private static readonly DateTime StartedUtc = Process.GetCurrentProcess().StartTime.ToUniversalTime();

    private readonly IConfiguration _config;
    private readonly IServerNode _node;
    private readonly IEnumerable<ITransport> _transports;
    private readonly IFleet? _fleet;
    private readonly IServer? _server;
    private readonly ICodecResolver? _codecs;
    private readonly IConnectionStore? _connections;
    private readonly IHostEnvironment? _environment;

    /// <summary>Created by DI.</summary>
    public ArchitectureCardBuilder(
        IConfiguration config,
        IServerNode node,
        IEnumerable<ITransport> transports,
        IFleet? fleet = null,
        IServer? server = null,
        ICodecResolver? codecs = null,
        IConnectionStore? connections = null,
        IHostEnvironment? environment = null)
    {
        _config = config;
        _node = node;
        _transports = transports;
        _fleet = fleet;
        _server = server;
        _codecs = codecs;
        _connections = connections;
        _environment = environment;
    }

    /// <summary>The fleet backplane's kind, or null without a fleet.</summary>
    public string? BackplaneKind => (_fleet as IFleetInspection)?.BackplaneKind;

    /// <summary>This server's card, taken now.</summary>
    public async Task<ArchitectureCardDto> BuildAsync()
    {
        var connections = _connections is null
            ? new List<AltruistConnection>()
            : (await _connections.GetAllConnectionsDictAsync().ConfigureAwait(false)).Values.ToList();
        var self = _fleet?.Self;
        var capacity = _node.Capacity();
        return new ArchitectureCardDto(
            _node.NodeId,
            _fleet?.Cluster,
            _fleet?.Region ?? "",
            capacity.State.ToString(),
            capacity.Load,
            capacity.MaxLoad,
            _environment?.EnvironmentName ?? "",
            Environment.MachineName,
            Environment.ProcessId,
            self?.InternalAddress,
            self?.PublicAddress,
            StartedUtc,
            DateTime.UtcNow,
            Listeners(connections),
            Portals(connections),
            ArchitectureStores.Describe(_config, new ArchitectureServerScope(Environment.MachineName, _node.NodeId, _fleet?.Cluster, BackplaneKind), Vaults()));
    }

    private IReadOnlyList<ArchitectureListenerDto> Listeners(IReadOnlyList<AltruistConnection> connections)
    {
        var http = HttpAddresses();
        int CountOf(string transport) => connections.Count(c => c.TransportMode == transport);
        var listeners = http.Select(a => new ArchitectureListenerDto("http", a, null, null)).ToList();
        foreach (var transport in _transports.Select(t => t.TransportType.ToLowerInvariant()).Distinct().Order(StringComparer.Ordinal))
        {
            var addresses = transport == "websocket"
                ? http.Select(a => a.Replace("https://", "wss://").Replace("http://", "ws://"))
                : new[] { $"{transport}://{SocketHost()}:{_config[$"altruist:server:transport:{transport}:port"] ?? DefaultSocketPort(transport)}" };
            listeners.AddRange(addresses.Select(a => new ArchitectureListenerDto(transport, a, CodecOf(transport), CountOf(transport))));
        }
        return listeners;
    }

    private static IReadOnlyList<ArchitecturePortalDto> Portals(IReadOnlyList<AltruistConnection> connections) =>
        PortalGateRegistry<IPortal>.GetAllHandlers()
            .Select(p => new ArchitecturePortalDto(
                p.Route,
                p.GetType().Name,
                connections.Count(c => c.Route.TrimEnd('/') == p.Route.TrimEnd('/'))))
            .DistinctBy(p => (p.Route, p.Type))
            .OrderBy(p => p.Route, StringComparer.Ordinal)
            .ThenBy(p => p.Type, StringComparer.Ordinal)
            .ToList();

    private static IReadOnlyList<ArchitectureVaultBinding> Vaults() =>
        VaultRegistry.GetAll()
            .Select(md => (md, attr: md.ClrType.GetCustomAttribute<VaultAttribute>()))
            .Select(v => new ArchitectureVaultBinding($"{v.md.Keyspace}.{v.attr?.Name ?? v.md.ClrType.Name}", v.attr?.DbInstance ?? ""))
            .ToList();

    private IReadOnlyList<string> HttpAddresses()
    {
        var bound = _server?.Features.Get<IServerAddressesFeature>()?.Addresses;
        if (bound is { Count: > 0 })
            return bound.Order(StringComparer.Ordinal).ToList();
        var host = _config["altruist:server:http:host"] ?? "localhost";
        var port = _config["altruist:server:http:port"] ?? "8080";
        return new[] { $"http://{host}:{port}" };
    }

    private string SocketHost() => _config["altruist:server:http:host"] ?? "localhost";

    private static string DefaultSocketPort(string transport) => transport == "udp" ? "13001" : "13000";

    private string? CodecOf(string transport) =>
        _codecs?.Resolve(transport).GetType().GetCustomAttribute<CodecProviderAttribute>()?.Name;
}
