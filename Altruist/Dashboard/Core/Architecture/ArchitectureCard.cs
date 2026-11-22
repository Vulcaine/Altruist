/*
Copyright 2025 Aron Gere
Licensed under the Apache License, Version 2.0
*/

namespace Altruist.Dashboard;

/// <summary>
/// What one server is made of and what it connects to: where clients reach it, its portals, and the stores it uses
/// (databases, cache, fleet backplane). Every dashboard-enabled server of a fleet publishes its card to the backplane
/// (<see cref="ArchitectureCardPublisher"/>), so the dashboard of any of them draws the whole fleet.
/// </summary>
/// <param name="NodeId">The server's id.</param>
/// <param name="Cluster">Fleet cluster; null when the server has no fleet.</param>
/// <param name="Region">Fleet region label (empty when unset).</param>
/// <param name="State">Server state (<c>Starting</c>, <c>Ready</c>, <c>Full</c>, <c>Draining</c>, <c>Drained</c>).</param>
/// <param name="Load">Current load in the server's load units.</param>
/// <param name="MaxLoad">Load budget; 0 or less means unlimited.</param>
/// <param name="Environment">Host environment (<c>Development</c>, <c>Production</c> …).</param>
/// <param name="Machine">Machine name.</param>
/// <param name="ProcessId">Process id.</param>
/// <param name="InternalAddress">Where the other servers reach it (relay target).</param>
/// <param name="PublicAddress">Its own client-facing address, when it has one.</param>
/// <param name="StartedUtc">Process start.</param>
/// <param name="PublishedUtc">When the card was taken.</param>
/// <param name="Listeners">Where clients connect.</param>
/// <param name="Portals">Socket portals by route.</param>
/// <param name="Stores">Databases, cache and backplane the server uses.</param>
public sealed record ArchitectureCardDto(
    string NodeId,
    string? Cluster,
    string Region,
    string State,
    double Load,
    double MaxLoad,
    string Environment,
    string Machine,
    int ProcessId,
    string? InternalAddress,
    string? PublicAddress,
    DateTime StartedUtc,
    DateTime PublishedUtc,
    IReadOnlyList<ArchitectureListenerDto> Listeners,
    IReadOnlyList<ArchitecturePortalDto> Portals,
    IReadOnlyList<ArchitectureStoreDto> Stores);

/// <summary>A place clients connect to.</summary>
/// <param name="Transport"><c>http</c>, <c>websocket</c>, <c>tcp</c> or <c>udp</c>.</param>
/// <param name="Address">URL or <c>host:port</c>.</param>
/// <param name="Codec">Packet codec of the transport (none for HTTP).</param>
/// <param name="Connections">Open connections over it (none for HTTP).</param>
public sealed record ArchitectureListenerDto(string Transport, string Address, string? Codec, int? Connections);

/// <summary>A socket portal.</summary>
/// <param name="Route">Route (e.g. <c>/game</c>).</param>
/// <param name="Type">Portal class name.</param>
/// <param name="Connections">Open connections on the route.</param>
public sealed record ArchitecturePortalDto(string Route, string Type, int Connections);

/// <summary>
/// One use of a store by the server: a database instance, the cache or the fleet backplane. Uses of one physical
/// store share a <see cref="Target"/> (the default database and an instance on the same database, or a Redis that is
/// both cache and backplane), and servers that use the same store report the same <see cref="Target"/>.
/// </summary>
/// <param name="Id">Identity of the use: <see cref="Target"/> and <see cref="Name"/>.</param>
/// <param name="Target">Identity of the physical store: provider, endpoints and database; a loopback endpoint also
/// carries the machine name (it is another store on another machine), an in-memory store the server's id.</param>
/// <param name="Provider"><c>postgres</c>, <c>redis</c>, <c>memory</c> …</param>
/// <param name="Name"><c>default</c> or the <c>instances</c> item's name for a database, else <c>cache</c> or <c>backplane</c>.</param>
/// <param name="Use"><c>database</c>, <c>cache</c> or <c>backplane</c>.</param>
/// <param name="Endpoints"><c>host:port</c> of each endpoint (credentials and options left out).</param>
/// <param name="Database">Database name, for SQL stores.</param>
/// <param name="Role"><c>readwrite</c> or <c>readonly</c>, for databases.</param>
/// <param name="Local">In this process's memory: never shared with another server.</param>
/// <param name="Vaults">Vaults stored through it (<c>keyspace.name</c>).</param>
public sealed record ArchitectureStoreDto(
    string Id,
    string Target,
    string Provider,
    string Name,
    string Use,
    IReadOnlyList<string> Endpoints,
    string? Database,
    string? Role,
    bool Local,
    IReadOnlyList<string> Vaults);

/// <summary>Response of <c>GET /dashboard/v1/architecture</c>.</summary>
/// <param name="Self">This server's id.</param>
/// <param name="Cluster">Fleet cluster; null without a fleet.</param>
/// <param name="BackplaneKind">The fleet backplane's kind (<c>memory</c>, <c>redis</c>).</param>
/// <param name="Shared">The backplane is shared: other servers' cards can be seen.</param>
/// <param name="Nodes">Every live server of the fleet (heartbeat view).</param>
/// <param name="Cards">This server's card (taken now) and the last published card of every other server on the
/// backplane, of this cluster and of the others (independent fleets, channels) sharing it.</param>
public sealed record ArchitectureReportDto(
    string Self,
    string? Cluster,
    string? BackplaneKind,
    bool Shared,
    IReadOnlyList<FleetNodeInfo> Nodes,
    IReadOnlyList<ArchitectureCardDto> Cards);

/// <summary>A vault and the database instance it names (<c>[Vault(DbInstance = …)]</c>; empty for the default).</summary>
/// <param name="Name">Display name (<c>keyspace.name</c>).</param>
/// <param name="DbInstance">Instance name; empty for the default database.</param>
public sealed record ArchitectureVaultBinding(string Name, string DbInstance);
