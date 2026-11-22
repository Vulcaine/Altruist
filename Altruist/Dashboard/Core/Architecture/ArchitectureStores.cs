/*
Copyright 2025 Aron Gere
Licensed under the Apache License, Version 2.0
*/

using Microsoft.Extensions.Configuration;

namespace Altruist.Dashboard;

/// <summary>The server whose stores are described.</summary>
/// <param name="Machine">Machine name (identity of loopback endpoints).</param>
/// <param name="NodeId">Server id (identity of in-memory stores).</param>
/// <param name="Cluster">Fleet cluster; null without a fleet.</param>
/// <param name="BackplaneKind">The fleet backplane's kind; null without a fleet.</param>
public sealed record ArchitectureServerScope(string Machine, string NodeId, string? Cluster, string? BackplaneKind);

/// <summary>
/// The stores a server uses, read from the keys that wire them: <c>altruist:persistence:database</c> (and its
/// <c>instances</c>), <c>altruist:persistence:cache:provider</c> with <c>altruist:persistence:redis:connection-string</c>,
/// and the fleet backplane (<c>altruist:server:fleet:redis</c>). Credentials and connection options are never read
/// into the result.
/// </summary>
public static class ArchitectureStores
{
    private const string DatabaseKey = "altruist:persistence:database";

    /// <summary>Every use of a store by this server.</summary>
    /// <param name="config">Configuration.</param>
    /// <param name="server">The server: where it runs, its fleet.</param>
    /// <param name="vaults">Registered vaults and the database instance each names.</param>
    public static IReadOnlyList<ArchitectureStoreDto> Describe(IConfiguration config, ArchitectureServerScope server, IReadOnlyList<ArchitectureVaultBinding> vaults)
    {
        var scope = new EndpointScope(server.Machine, server.NodeId);
        return Databases(config, scope, vaults)
            .Concat(Cache(config, scope))
            .Concat(Backplane(config, scope, server))
            .ToList();
    }

    private static IEnumerable<ArchitectureStoreDto> Databases(IConfiguration config, EndpointScope scope, IReadOnlyList<ArchitectureVaultBinding> vaults)
    {
        var provider = Lower(config[$"{DatabaseKey}:provider"]);
        if (provider.Length == 0)
            yield break;

        var instances = config.GetSection($"{DatabaseKey}:instances").GetChildren()
            .Select(item => (Name: item["name"] ?? "", Section: (IConfiguration)item))
            .Where(i => i.Name.Length > 0)
            .ToList();
        var instanceNames = instances.Select(i => i.Name).ToHashSet(StringComparer.Ordinal);
        // A vault naming an unknown instance is built on the default database.
        string InstanceOf(ArchitectureVaultBinding v) => instanceNames.Contains(v.DbInstance) ? v.DbInstance : "default";
        IReadOnlyList<string> VaultsOf(string name) =>
            vaults.Where(v => InstanceOf(v) == name).Select(v => v.Name).Order(StringComparer.Ordinal).ToList();

        yield return Database(provider, "default", config.GetSection(DatabaseKey), scope, VaultsOf("default"));
        foreach (var (name, section) in instances)
            yield return Database(provider, name, section, scope, VaultsOf(name));
    }

    private static ArchitectureStoreDto Database(string provider, string name, IConfiguration section, EndpointScope scope, IReadOnlyList<string> vaults)
    {
        var endpoints = provider == "postgres"
            ? new[] { Endpoint(section["host"], section["port"], 5432) }
            : Endpoints(section["contact-points"] ?? section["host"], DefaultPortOf(provider));
        var database = Blank(section["database"]) ?? Blank(section["keyspace"]);
        var role = Lower(section["role"]) is { Length: > 0 } r ? r : "readwrite";
        var target = scope.Target(provider, endpoints, database);
        return new ArchitectureStoreDto($"{target}#{name}", target, provider, name, "database", endpoints, database, role, false, vaults);
    }

    private static IEnumerable<ArchitectureStoreDto> Cache(IConfiguration config, EndpointScope scope)
    {
        var provider = Lower(config["altruist:persistence:cache:provider"]);
        if (provider.Length == 0)
            yield break;
        yield return provider == "redis"
            ? Remote("redis", "cache", Endpoints(config["altruist:persistence:redis:connection-string"], 6379), scope)
            : scope.InMemory(provider, "cache");
    }

    // Fleets of several clusters may share one backplane store, each in its own key space: one use per cluster.
    private static IEnumerable<ArchitectureStoreDto> Backplane(IConfiguration config, EndpointScope scope, ArchitectureServerScope server)
    {
        if (server.BackplaneKind is not { } kind)
            yield break;
        if (Lower(kind) != "redis")
        {
            yield return scope.InMemory(Lower(kind), "backplane");
            yield break;
        }
        var store = Remote("redis", "backplane", Endpoints(config["altruist:server:fleet:redis"], 6379), scope);
        var name = server.Cluster ?? store.Name;
        yield return store with { Id = $"{store.Id}:{name}", Name = name };
    }

    private static ArchitectureStoreDto Remote(string provider, string use, IReadOnlyList<string> endpoints, EndpointScope scope)
    {
        var target = scope.Target(provider, endpoints, null);
        return new ArchitectureStoreDto($"{target}#{use}", target, provider, use, use, endpoints, null, null, false, Array.Empty<string>());
    }

    /// <summary>
    /// The <c>host:port</c> endpoints of a connection string such as StackExchange.Redis's
    /// (<c>"a:6379,b:6380,password=…,ssl=true"</c>) or a contact-point list: every comma-separated part that is not an
    /// option (<c>key=value</c>), with <paramref name="defaultPort"/> where the part has none.
    /// </summary>
    /// <param name="connectionString">Connection string; null or blank gives none.</param>
    /// <param name="defaultPort">Port for a part without one.</param>
    public static IReadOnlyList<string> Endpoints(string? connectionString, int defaultPort) =>
        (connectionString ?? "")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(part => !part.Contains('='))
            .Select(part => HasPort(part) ? part : $"{part}:{defaultPort}")
            .ToList();

    private static string Endpoint(string? host, string? port, int defaultPort) =>
        $"{Blank(host) ?? "localhost"}:{(int.TryParse(port, out var p) && p > 0 ? p : defaultPort)}";

    // "host:port" and "[::1]:port" have a port; a bare IPv6 address ("::1") does not.
    private static bool HasPort(string part) =>
        part.StartsWith('[') ? part.Contains("]:", StringComparison.Ordinal) : part.Count(c => c == ':') == 1;

    private static int DefaultPortOf(string provider) => provider switch
    {
        "scylladb" or "cassandra" => 9042,
        "mysql" => 3306,
        _ => 5432,
    };

    private static string Lower(string? value) => (value ?? "").Trim().ToLowerInvariant();

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private sealed record EndpointScope(string Machine, string NodeId)
    {
        public string Target(string provider, IReadOnlyList<string> endpoints, string? database)
        {
            var hosts = string.Join(",", endpoints.Select(Qualified).Order(StringComparer.Ordinal));
            return database is null ? $"{provider}://{hosts}" : $"{provider}://{hosts}/{database}";
        }

        public ArchitectureStoreDto InMemory(string provider, string use)
        {
            var target = $"{provider}@{NodeId}";
            return new ArchitectureStoreDto($"{target}#{use}", target, provider, use, use, Array.Empty<string>(), null, null, true, Array.Empty<string>());
        }

        // A loopback endpoint is this machine's own store.
        private string Qualified(string endpoint)
        {
            var host = endpoint.StartsWith('[') ? endpoint[..(endpoint.IndexOf(']') + 1)] : endpoint.Split(':')[0];
            var lower = host.ToLowerInvariant();
            var loopback = lower is "localhost" or "[::1]" || lower.StartsWith("127.", StringComparison.Ordinal);
            var normalized = loopback ? $"loopback{endpoint[host.Length..]}@{Machine}" : endpoint.ToLowerInvariant();
            return normalized;
        }
    }
}
