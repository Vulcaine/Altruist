/* 
Copyright 2025 Aron Gere

Licensed under the Apache License, Version 2.0 (the "License");
You may obtain a copy at http://www.apache.org/licenses/LICENSE-2.0
*/

using System.Reflection;
using System.Text;

using Altruist.Contracts;
using Altruist.Persistence;
using Altruist.UORM;

using Cassandra;
using Cassandra.Mapping;

namespace Altruist.ScyllaDB;

[Service(typeof(IScyllaDbProvider))]
[Service(typeof(IGeneralDatabaseProvider))]
[ConditionalOnConfig("altruist:persistence:database:provider", havingValue: "scylladb")]
public class ScyllaDbProvider : IScyllaDbProvider
{
    private ISession? _session { get; set; }
    private IMapper? _mapper { get; set; }
    private Cluster? _cluster { get; set; }
    private readonly List<string> _contactPoints;
    private readonly Builder? _builder;
    public bool IsConnected { get; set; }

    public string ServiceName { get; } = "ScyllaDB";
    public IDatabaseServiceToken Token { get; private set; } = ScyllaDBToken.Instance;

    public ScyllaDbProvider(
        [AppConfigValue("altruist:persistence:database:contact-points")] List<string> contactPoints,
        Builder? builder = null)
    {
        _contactPoints = contactPoints;
        _builder = builder;
    }

    #region Connection Events

    private async Task ensureConnected()
    {
        if (!IsConnected)
        {
            await ConnectAsync();
        }
    }

    public event Action<Host> HostAdded
    {
        add { if (_cluster != null) _cluster.HostAdded += value; }
        remove { if (_cluster != null) _cluster.HostAdded -= value; }
    }

    public event Action<Host> HostRemoved
    {
        add { if (_cluster != null) _cluster.HostRemoved += value; }
        remove { if (_cluster != null) _cluster.HostRemoved -= value; }
    }

    public event Action? OnConnected;
    public event Action<Exception>? OnFailed;
    public event Action<Exception>? OnRetryExhausted;

    public void RaiseConnectedEvent()
    {
        IsConnected = true;
        OnConnected?.Invoke();
    }

    public void RaiseFailedEvent(Exception ex)
    {
        IsConnected = false;
        OnFailed?.Invoke(ex);
    }

    public void RaiseOnRetryExhaustedEvent(Exception ex)
    {
        IsConnected = false;
        OnRetryExhausted?.Invoke(ex);
    }

    public async Task ShutdownAsync(Exception? ex = null)
    {
        StopHealthChecks();
        if (_session == null)
            return;
        await _session.ShutdownAsync();
    }

    public async Task ConnectAsync(int maxRetries = 30, int delayMilliseconds = 2000)
    {
        if (_contactPoints is null || _contactPoints.Count == 0 || IsConnected)
            return;

        var clusterBuilder = _builder ?? Cluster.Builder().WithDefaultKeyspace("altruist");

        foreach (var cp in _contactPoints)
        {
            var withScheme = cp.Contains("://") ? cp : "cql://" + cp;
            var uri = new Uri(withScheme);
            clusterBuilder = clusterBuilder
                .AddContactPoint(uri.Host)
                .WithPort(uri.Port)
                .WithReconnectionPolicy(new ConstantReconnectionPolicy(10000))
                .WithRetryPolicy(new AltruistScyllaDefaultRetryPolicy(this));
        }

        var cluster = clusterBuilder.Build();

        Exception? lastException = null;
        for (int attempt = 1; attempt <= maxRetries; attempt++)
        {
            try
            {
                _session = cluster.ConnectAndCreateDefaultKeyspaceIfNotExists();
                _cluster = cluster;
                _mapper = new Mapper(_session);
                RaiseConnectedEvent();
                StartHealthChecks();
                return;
            }
            catch (Exception ex)
            {
                if (attempt == 1)
                    RaiseFailedEvent(ex);
                lastException = ex;
                if (attempt < maxRetries)
                    await Task.Delay(delayMilliseconds).ConfigureAwait(false);
            }
        }

        RaiseOnRetryExhaustedEvent(lastException!);
    }

    public async Task ConnectAsync(string protocol, string host, int port, int maxRetries = 30, int delayMilliseconds = 2000)
    {
        if (IsConnected)
            return;

        var clusterBuilder = (_builder ?? Cluster.Builder().WithDefaultKeyspace("altruist"))
            .AddContactPoint(host)
            .WithPort(port)
            .WithReconnectionPolicy(new ConstantReconnectionPolicy(10000))
            .WithRetryPolicy(new AltruistScyllaDefaultRetryPolicy(this));

        var cluster = clusterBuilder.Build();

        Exception? lastException = null;
        for (int attempt = 1; attempt <= maxRetries; attempt++)
        {
            try
            {
                _session = await Task.Run(() => cluster.ConnectAndCreateDefaultKeyspaceIfNotExists());
                _cluster = cluster;
                _mapper = new Mapper(_session);
                RaiseConnectedEvent();
                StartHealthChecks();
                return;
            }
            catch (Exception ex)
            {
                if (attempt == 1)
                    RaiseFailedEvent(ex);
                lastException = ex;
                if (attempt < maxRetries)
                    await Task.Delay(delayMilliseconds).ConfigureAwait(false);
            }
        }

        RaiseOnRetryExhaustedEvent(lastException!);
    }

    public Task ConnectAsync() => ConnectAsync(maxRetries: 30, delayMilliseconds: 2000);

    #endregion

    #region  Provider API

    private async Task<IEnumerable<TVaultModel>> ExecuteFetchAsync<TVaultModel>(
        string cqlQuery,
        List<object>? parameters = null) where TVaultModel : class, IVaultModel
    {
        try
        {
            await ensureConnected();

            if ((parameters?.Count ?? 0) == 0)
                return await _mapper!.FetchAsync<TVaultModel>(cqlQuery);

            return await _mapper!.FetchAsync<TVaultModel>(cqlQuery, parameters);
        }
        catch (Exception ex)
        {
            RaiseFailedEvent(ex);
            throw;
        }
        finally
        {
            if (IsConnected)
            {
                RaiseConnectedEvent();
            }
        }
    }

    public async Task<IEnumerable<TVaultModel>> QueryAsync<TVaultModel>(
        string cqlQuery, List<object>? parameters = null) where TVaultModel : class, IVaultModel
        => (await ExecuteFetchAsync<TVaultModel>(cqlQuery, parameters).ConfigureAwait(false)).ToList();

    public async Task<TVaultModel?> QuerySingleAsync<TVaultModel>(
        string cqlQuery, List<object>? parameters = null) where TVaultModel : class, IVaultModel
        => (await ExecuteFetchAsync<TVaultModel>(cqlQuery, parameters).ConfigureAwait(false)).FirstOrDefault();

    public async Task<long> ExecuteCountAsync(string cqlQuery, List<object>? parameters = null)
    {
        try
        {
            await ensureConnected();
            var statement = _session!.Prepare(cqlQuery).Bind(parameters?.ToArray());
            var result = await _session.ExecuteAsync(statement).ConfigureAwait(false);
            var row = result.FirstOrDefault();
            return row != null ? row.GetValue<long>("count") : 0;
        }
        catch (Exception ex)
        {
            RaiseFailedEvent(ex);
            throw;
        }
    }

    public async Task<long> ExecuteAsync(string cqlQuery, List<object>? parameters = null)
    {
        try
        {
            await ensureConnected();
            var count = parameters?.Count ?? 0;
            var statement = _session!.Prepare(cqlQuery).Bind(count == 0 ? null : parameters?.ToArray());
            var result = await _session.ExecuteAsync(statement).ConfigureAwait(false);
            return result.GetRows().Count();
        }
        catch (Exception ex)
        {
            RaiseFailedEvent(ex);
            throw;
        }
        finally
        {
            if (!IsConnected)
                RaiseConnectedEvent();
        }
    }

    public async Task<long> UpdateAsync<TVaultModel>(TVaultModel entity) where TVaultModel : class, IVaultModel
    {
        await ensureConnected();
        await _mapper!.UpdateAsync(entity).ConfigureAwait(false);
        return 1;
    }

    public async Task<long> DeleteAsync<TVaultModel>(TVaultModel entity) where TVaultModel : class, IVaultModel
    {
        await ensureConnected();
        await _mapper!.DeleteAsync(entity).ConfigureAwait(false);
        return 1;
    }

    public async Task CreateTableAsync<TVaultModel>(IKeyspace? keyspace = null) where TVaultModel : class, IVaultModel
        => await CreateTableAsync(typeof(TVaultModel), keyspace).ConfigureAwait(false);

    private static readonly Type OpenGeneric_List = typeof(List<>);
    private static readonly Type OpenGeneric_IList = typeof(IList<>);
    private static readonly Type OpenGeneric_IReadOnlyList = typeof(IReadOnlyList<>);
    private static readonly Type OpenGeneric_HashSet = typeof(HashSet<>);
    private static readonly Type OpenGeneric_ISet = typeof(ISet<>);
    private static readonly Type OpenGeneric_Dictionary = typeof(Dictionary<,>);
    private static readonly Type OpenGeneric_IDictionary = typeof(IDictionary<,>);
    private static readonly Type OpenGeneric_IReadOnlyDictionary = typeof(IReadOnlyDictionary<,>);
    private static readonly Type OpenGeneric_Nullable = typeof(Nullable<>);

    private string MapTypeToCql(Type type)
    {
        // Treat Nullable<T> as T
        if (type.IsGenericType && type.GetGenericTypeDefinition() == OpenGeneric_Nullable)
            type = Nullable.GetUnderlyingType(type)!;

        // byte[] must remain blob, not list<tinyint>
        if (type == typeof(byte[]))
            return "blob";

        // Arrays -> list<elem> (except byte[])
        if (type.IsArray)
        {
            var e = type.GetElementType()!;
            var elem = MapPrimitiveOrNull(e);
            return elem != null ? $"list<{elem}>" : "text";
        }

        // Lists -> list<elem>
        if (type.IsGenericType &&
            (type.GetGenericTypeDefinition() == OpenGeneric_List ||
             type.GetGenericTypeDefinition() == OpenGeneric_IList ||
             type.GetGenericTypeDefinition() == OpenGeneric_IReadOnlyList))
        {
            var e = type.GetGenericArguments()[0];
            var elem = MapPrimitiveOrNull(e);
            return elem != null ? $"list<{elem}>" : "text";
        }

        // Sets -> set<elem>
        if (type.IsGenericType &&
            (type.GetGenericTypeDefinition() == OpenGeneric_HashSet ||
             type.GetGenericTypeDefinition() == OpenGeneric_ISet))
        {
            var e = type.GetGenericArguments()[0];
            var elem = MapPrimitiveOrNull(e);
            return elem != null ? $"set<{elem}>" : "text";
        }

        // Dictionaries -> map<k,v>
        if (type.IsGenericType &&
            (type.GetGenericTypeDefinition() == OpenGeneric_Dictionary ||
             type.GetGenericTypeDefinition() == OpenGeneric_IDictionary ||
             type.GetGenericTypeDefinition() == OpenGeneric_IReadOnlyDictionary))
        {
            var args = type.GetGenericArguments();
            var k = MapPrimitiveOrNull(args[0]);
            var v = MapPrimitiveOrNull(args[1]);
            return (k != null && v != null) ? $"map<{k},{v}>" : "text";
        }

        // Direct primitives
        var direct = MapPrimitiveOrNull(type);
        if (direct != null)
            return direct;

        // Fallback for complex types (records, your Prefab*Ref structs, etc.) – store as JSON text
        return "text";
    }

    // Return CQL name for a supported scalar, otherwise null.
    // Keep this tiny and predictable. You can extend later.
    private static string? MapPrimitiveOrNull(Type t) => t switch
    {
        // textual
        { } when t == typeof(string) => "text",
        // integral
        { } when t == typeof(long) => "bigint",
        { } when t == typeof(int) => "int",
        { } when t == typeof(short) => "smallint",
        { } when t == typeof(sbyte) || t == typeof(byte) => "tinyint",
        // numeric
        { } when t == typeof(double) => "double",
        { } when t == typeof(float) => "float",
        { } when t == typeof(decimal) => "decimal",
        // boolean
        { } when t == typeof(bool) => "boolean",
        // time & uuid
        { } when t == typeof(DateTime) => "timestamp",         // store as UTC
        { } when t == typeof(DateTimeOffset) => "timestamp",   // store as UTC
        { } when t == typeof(Guid) => "uuid",                  // if you need time ordering, use timeuuid in your model instead
                                                               // networking
        { } when t.FullName == "System.Net.IPAddress" => "inet",
        // big ints
        { } when t == typeof(System.Numerics.BigInteger) => "varint",
        // durations (if you want to keep ticks, keep bigint instead; otherwise map to CQL 'duration' with a custom converter)
        // { } when t == typeof(TimeSpan) => "duration",
        { } when t == typeof(TimeSpan) => null, // prefer custom converter (ticks->bigint) or duration with driver-specific binding
                                                // byte[] handled earlier
        _ => null
    };

    public async Task CreateKeySpaceAsync(string keyspace, ReplicationOptions? options = null)
    {
        await ensureConnected();
        var actual = options as ScyllaReplicationOptions ?? new ScyllaReplicationOptions();

        Dictionary<string, string> replicationConfig;
        if (actual.Strategy == ReplicationStrategy.SimpleStrategy)
        {
            replicationConfig = new Dictionary<string, string>
            {
                { "class", "SimpleStrategy" },
                { "replication_factor", actual.ReplicationFactor.ToString() }
            };
        }
        else if (actual.Strategy == ReplicationStrategy.NetworkTopologyStrategy && actual.DataCenters != null)
        {
            replicationConfig = new Dictionary<string, string> { { "class", "NetworkTopologyStrategy" } };
            foreach (var dc in actual.DataCenters)
                replicationConfig.Add(dc.Key, dc.Value.ToString());
        }
        else
        {
            throw new ArgumentException("Invalid replication options: NetworkTopologyStrategy requires at least one data center.");
        }

        _session!.CreateKeyspaceIfNotExists(keyspace, replicationConfig);
    }

    public async Task CreateTableAsync(Type entityType, IKeyspace? keyspace = null)
    {
        await ensureConnected();

        var document = Document.From(entityType);
        var tableAttr = entityType.GetCustomAttribute<VaultAttribute>()
                       ?? throw new InvalidOperationException($"Type '{entityType.Name}' is  missing VaultAttribute.");

        if (keyspace is not IScyllaKeyspace)
            throw new InvalidOperationException("Keyspace must be of type IScyllaKeyspace.");

        var ks = keyspace as IScyllaKeyspace ?? new DefaultScyllaKeyspace();
        await CreateKeySpaceAsync(ks.Name, ks.Options).ConfigureAwait(false);

        string tableName = document.Name;
        bool storeHistory = tableAttr.StoreHistory;

        // --- simplified PK resolution via ReflectionUtils ---
        var keyColumns = ReflectionUtils.GetPrimaryKeyColumns(entityType);
        if (keyColumns.Count == 0)
            throw new InvalidOperationException($"PrimaryKeyAttribute is required on '{entityType.Name}' or its base types.");

        // Sorting key
        var sortingAttr = document.SortingBy;
        var sortingKey = ReflectionUtils.ResolveSortingColumnName(document, entityType);
        bool sortAscending = sortingAttr?.Ascending ?? true;

        // === Define Table Columns ===
        var mappableProps = ReflectionUtils.GetMappableProperties(entityType);
        var columns = mappableProps.Select(p =>
        {
            var colName = ReflectionUtils.GetColumnName(p);
            var colType = MapTypeToCql(p.PropertyType);
            return $"{colName} {colType}";
        });

        // === CREATE MAIN TABLE ===
        var sb = new StringBuilder();
        sb.Append($"CREATE TABLE IF NOT EXISTS {ks.Name}.{tableName} (");
        sb.Append(string.Join(", ", columns));

        if (sortingKey != null)
        {
            sb.Append($", PRIMARY KEY (({string.Join(", ", keyColumns)}), {sortingKey})");
            sb.Append($") WITH CLUSTERING ORDER BY ({sortingKey} {(sortAscending ? "ASC" : "DESC")});");
        }
        else
        {
            sb.Append($", PRIMARY KEY ({string.Join(", ", keyColumns)}) );");
        }

        await _session!.ExecuteAsync(new SimpleStatement(sb.ToString())).ConfigureAwait(false);

        // === CREATE SECONDARY INDEXES FROM [VaultColumnIndex] ===
        if (document.Indexes is not null && document.Indexes.Count > 0)
        {
            var pkSet = new HashSet<string>(keyColumns, StringComparer.OrdinalIgnoreCase);

            foreach (var idxCol in document.Indexes.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                // skip if it's part of the primary key (no index needed/allowed)
                if (pkSet.Contains(idxCol))
                    continue;

                var indexName = $"{tableName}_{idxCol}_idx";
                var indexCql = $"CREATE INDEX IF NOT EXISTS {indexName} ON {ks.Name}.{tableName} ({idxCol});";
                await _session.ExecuteAsync(new SimpleStatement(indexCql)).ConfigureAwait(false);
            }
        }

        // === CREATE HISTORY TABLE (if enabled) ===
        if (storeHistory)
        {
            string historyTable = $"{tableName}_history";

            if (!await TableExistsAsync(ks.Name, historyTable).ConfigureAwait(false))
            {
                sb.Clear();
                sb.Append($"CREATE TABLE IF NOT EXISTS {ks.Name}.{historyTable} (");
                sb.Append(string.Join(", ", columns));
                sb.Append(", timestamp TIMESTAMP");
                sb.Append($", PRIMARY KEY (({string.Join(", ", keyColumns)}), timestamp)");
                sb.Append(") WITH CLUSTERING ORDER BY (timestamp DESC);");

                await _session.ExecuteAsync(new SimpleStatement(sb.ToString())).ConfigureAwait(false);

                foreach (var key in keyColumns)
                {
                    var indexQuery = $@"CREATE INDEX IF NOT EXISTS ON {ks.Name}.{historyTable} ({key});";
                    await _session.ExecuteAsync(new SimpleStatement(indexQuery)).ConfigureAwait(false);
                }
            }
        }
    }

    private async Task<bool> TableExistsAsync(string keyspace, string tableName)
    {
        await ensureConnected();
        var query = $"SELECT table_name FROM system_schema.tables WHERE keyspace_name = '{keyspace}' AND table_name = '{tableName}';";
        var result = await _session!.ExecuteAsync(new SimpleStatement(query)).ConfigureAwait(false);
        return result.Any();
    }

    public async Task ChangeKeyspaceAsync(string keyspace)
    {
        await ensureConnected();
        var query = $"USE {keyspace};";
        await _session!.ExecuteAsync(new SimpleStatement(query)).ConfigureAwait(false);
    }

    #endregion

    #region  Health Checks

    private CancellationTokenSource _healthCheckCts = new();
    private SemaphoreSlim _pingLock = new(1, 1);

    private void StartHealthChecks(int seconds = 5)
    {
        _ = Task.Run(async () =>
        {
            while (!_healthCheckCts.Token.IsCancellationRequested)
            {
                try
                {
                    await HealthCheckAsync();
                    await Task.Delay(TimeSpan.FromSeconds(seconds), _healthCheckCts.Token);
                }
                catch (OperationCanceledException)
                {
                    continue;
                }
            }
        });
    }

    private async Task HealthCheckAsync()
    {
        if (!await _pingLock.WaitAsync(0))
            return;
        try
        {
            if (_session != null)
            {
                await _session.ExecuteAsync(new SimpleStatement("SELECT now() FROM system.local")).ConfigureAwait(false);
                if (!IsConnected)
                {
                    IsConnected = true;
                    RaiseConnectedEvent();
                }
            }
        }
        catch (Exception ex)
        {
            if (IsConnected)
            {
                IsConnected = false;
                RaiseFailedEvent(ex);
            }
        }
        finally
        {
            _pingLock.Release();
        }
    }

    private void StopHealthChecks() => _healthCheckCts.Cancel();

    #endregion
}

[Service(typeof(IDatabaseVaultFactory))]
[Service(typeof(DatabaseVaultFactory))]
public class ScyllaVaultFactory : DatabaseVaultFactory
{
    public ScyllaVaultFactory(IScyllaDbProvider databaseProvider, IServiceProvider serviceProvider)
        : base(databaseProvider, serviceProvider) { }
}
