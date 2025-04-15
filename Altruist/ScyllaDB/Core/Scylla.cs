using System.Reflection;
using System.Text;
using Altruist.Contracts;
using Altruist.Database;
using Cassandra;
using Cassandra.Mapping;
using Altruist.UORM;

namespace Altruist.ScyllaDB;

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



    public ScyllaDbProvider(List<string> contactPoints, Builder? builder = null)
    {
        _contactPoints = contactPoints;
        _builder = builder;
    }


    public ScyllaDbProvider(ISession session, IMapper mapper, IDatabaseServiceToken token)
    {
        _contactPoints = new List<string>();
        _session = session;
        _mapper = mapper;
        Token = token;
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
        add
        {
            if (_cluster != null)
                _cluster.HostAdded += value;
        }
        remove
        {
            if (_cluster != null)
                _cluster.HostAdded -= value;
        }
    }

    public event Action<Host> HostRemoved
    {
        add
        {
            if (_cluster != null)
                _cluster.HostRemoved += value;
        }
        remove
        {
            if (_cluster != null)
                _cluster.HostRemoved -= value;
        }
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
        if (_contactPoints.Count == 0 || IsConnected)
            return;

        var clusterBuilder = _builder ?? Cluster.Builder().WithDefaultKeyspace("altruist");

        foreach (var contactPoint in _contactPoints)
        {
            string contactPointWithScheme = contactPoint.Contains("://") ? contactPoint : "cql://" + contactPoint;
            var uri = new Uri(contactPointWithScheme);
            string host = uri.Host;
            int port = uri.Port;

            clusterBuilder = clusterBuilder
                .AddContactPoint(host)
                .WithPort(port)
                .WithReconnectionPolicy(new ConstantReconnectionPolicy(10000))
                .WithRetryPolicy(new AltruistScyllaDefaultRetryPolicy(this));
        }

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
                {
                    RaiseFailedEvent(ex);
                }

                lastException = ex;

                if (attempt < maxRetries)
                {
                    await Task.Delay(delayMilliseconds);
                }
            }
        }

        RaiseOnRetryExhaustedEvent(lastException!);
    }

    #endregion

    #region  Provider API

    private async Task<IEnumerable<TVaultModel>> ExecuteFetchAsync<TVaultModel>(
    string cqlQuery,
    List<object>? parameters = null) where TVaultModel : class, IVaultModel
    {
        try
        {
            await ensureConnected();

            var count = parameters?.Count ?? 0;
            IEnumerable<TVaultModel> results;
            if (count == 0)
            {
                results = await _mapper!.FetchAsync<TVaultModel>(cqlQuery);
            }
            else
            {
                results = await _mapper!.FetchAsync<TVaultModel>(cqlQuery, parameters);
            }

            if (!IsConnected)
            {
                RaiseConnectedEvent();
            }

            return results;
        }
        catch (Exception ex)
        {
            RaiseFailedEvent(ex);
            throw;
        }
    }


    public async Task<IEnumerable<TVaultModel>> QueryAsync<TVaultModel>(
    string cqlQuery, List<object>? parameters = null) where TVaultModel : class, IVaultModel
    {
        return (await ExecuteFetchAsync<TVaultModel>(cqlQuery, parameters)).ToList();
    }

    public async Task<TVaultModel?> QuerySingleAsync<TVaultModel>(
        string cqlQuery, List<object>? parameters = null) where TVaultModel : class, IVaultModel
    {
        return (await ExecuteFetchAsync<TVaultModel>(cqlQuery, parameters)).FirstOrDefault();
    }

    public async Task<long> ExecuteCountAsync(string cqlQuery, List<object>? parameters = null)
    {
        try
        {
            await ensureConnected();

            var statement = _session!.Prepare(cqlQuery).Bind(parameters?.ToArray());
            var result = await _session.ExecuteAsync(statement);

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
            var result = await _session.ExecuteAsync(statement);

            if (!IsConnected)
            {
                RaiseConnectedEvent();
            }

            return result.GetRows().Count();
        }
        catch (Exception ex)
        {
            RaiseFailedEvent(ex);
            throw;
        }
    }


    public async Task<long> UpdateAsync<TVaultModel>(TVaultModel entity) where TVaultModel : class, IVaultModel
    {
        await ensureConnected();
        await _mapper!.UpdateAsync(entity);
        return 1;
    }

    public async Task<long> DeleteAsync<TVaultModel>(TVaultModel entity) where TVaultModel : class, IVaultModel
    {
        await ensureConnected();
        await _mapper!.DeleteAsync(entity);
        return 1;
    }

    public async Task CreateTableAsync<TVaultModel>(IKeyspace? keyspace = null) where TVaultModel : class, IVaultModel
    {
        var entityType = typeof(TVaultModel);
        await CreateTableAsync(entityType, keyspace);
    }

    private string MapTypeToCql(Type type)
    {
        // Check for array types (e.g., float[])
        if (type.IsArray)
        {
            var elementType = type.GetElementType();

            if (elementType == typeof(float))
            {
                return "list<float>"; // CQL for an array of floats (use list<float> in Cassandra)
            }
            else if (elementType == typeof(double))
            {
                return "list<double>"; // CQL for an array of doubles
            }
            else if (elementType == typeof(int))
            {
                return "list<int>"; // CQL for an array of ints
            }
            else if (elementType == typeof(string))
            {
                return "list<text>"; // CQL for an array of strings
            }
            else
            {
                throw new NotSupportedException($"Array type {elementType!.Name} is not supported.");
            }
        }

        // Standard type mappings
        return type switch
        {
            _ when type == typeof(string) => "text",
            _ when type == typeof(int) => "int",
            _ when type == typeof(Guid) => "uuid",
            _ when type == typeof(bool) => "boolean",
            _ when type == typeof(double) => "double", // Use double for double precision
            _ when type == typeof(float) => "float", // Use float for single precision
            _ when type == typeof(DateTime) => "timestamp",
            _ when type == typeof(byte[]) => "blob",
            _ when type == typeof(TimeSpan) => "bigint", // CQL for TimeSpan
            _ => throw new NotSupportedException($"Type {type.Name} is not supported.")
        };
    }

    public async Task CreateKeySpaceAsync(string keyspace, ReplicationOptions? options = null)
    {
        await ensureConnected();
        var actualOptions = options as ScyllaReplicationOptions ?? new ScyllaReplicationOptions();

        // Build the replication configuration
        Dictionary<string, string> replicationConfig;

        if (actualOptions.Strategy == ReplicationStrategy.SimpleStrategy)
        {
            replicationConfig = new Dictionary<string, string>
            {
                { "class", "SimpleStrategy" },
                { "replication_factor", actualOptions.ReplicationFactor.ToString() }
            };
        }
        else if (actualOptions.Strategy == ReplicationStrategy.NetworkTopologyStrategy && actualOptions.DataCenters != null)
        {
            replicationConfig = new Dictionary<string, string>
            {
                { "class", "NetworkTopologyStrategy" }
            };

            foreach (var dataCenter in actualOptions.DataCenters)
            {
                replicationConfig.Add(dataCenter.Key, dataCenter.Value.ToString());
            }
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
        var tableAttribute = entityType.GetCustomAttribute<VaultAttribute>();
        if (tableAttribute == null)
        {
            throw new InvalidOperationException($"Type '{entityType.Name}' is missing VaultAttribute.");
        }

        if (!(keyspace is IScyllaKeyspace))
        {
            throw new InvalidOperationException("Keyspace must be of type IScyllaKeyspace.");
        }

        var actualKeyspace = keyspace as IScyllaKeyspace ?? new DefaultScyllaKeyspace();

        await CreateKeySpaceAsync(actualKeyspace.Name, actualKeyspace.Options);

        string tableName = document.Name;
        bool storeHistory = tableAttribute.StoreHistory;

        var primaryKey = document.PrimaryKey;

        //  entityType.GetCustomAttribute<VaultPrimaryKeyAttribute>();
        if (primaryKey == null || primaryKey.Keys.Length == 0)
        {
            throw new InvalidOperationException($"PrimaryKeyAttribute is required on '{entityType.Name}'.");
        }

        // === Get Sorting Key from Class-Level Attribute ===
        var sortingAttribute = document.SortingBy;
        var sortingKey = sortingAttribute != null && document.Columns.ContainsKey(sortingAttribute.Name) ? document.Columns[sortingAttribute.Name] : sortingAttribute?.Name.ToLower();

        // entityType.GetCustomAttribute<VaultSortingByAttribute>();
        bool sortAscending = sortingAttribute?.Ascending ?? true;

        // Validate Sorting Key (if defined)
        if (sortingKey != null && !entityType.GetProperties().Any(p => p.Name.ToLower() == sortingKey))
        {
            throw new InvalidOperationException($"Sorting key '{sortingKey}' is not a property of '{entityType.Name}'.");
        }

        // === Define Table Columns ===
        var columns =
            entityType.GetProperties()
                .Where(p => p.GetCustomAttribute<VaultIgnoredAttribute>() == null)
                .Select(p =>
                {
                    var columnAttr = p.GetCustomAttribute<VaultColumnAttribute>();
                    string columnName = columnAttr?.Name ?? p.Name.ToLower();
                    string columnType = MapTypeToCql(p.PropertyType);
                    return $"{columnName} {columnType}";
                });

        // === STEP 1: CREATE MAIN TABLE ===
        var sb = new StringBuilder();
        sb.Append($"CREATE TABLE IF NOT EXISTS {actualKeyspace.Name}.{tableName} (");
        sb.Append(string.Join(", ", columns));

        // Define PRIMARY KEY (if sorting is needed)
        if (sortingKey != null)
        {
            sb.Append($", PRIMARY KEY (({string.Join(", ", primaryKey.Keys)}), {sortingKey})");
            sb.Append($") WITH CLUSTERING ORDER BY ({sortingKey} {(sortAscending ? "ASC" : "DESC")});");
        }
        else
        {
            sb.Append($", PRIMARY KEY ({string.Join(", ", primaryKey.Keys)}) );");
        }

        await _session!.ExecuteAsync(new SimpleStatement(sb.ToString()));

        // === STEP 2: CREATE HISTORY TABLE (if enabled) ===
        if (storeHistory)
        {
            string historyTableName = $"{tableName}_history";

            if (!await TableExistsAsync(actualKeyspace.Name, historyTableName))
            {
                sb.Clear();
                sb.Append($"CREATE TABLE IF NOT EXISTS {actualKeyspace.Name}.{historyTableName} (");
                sb.Append(string.Join(", ", columns));
                sb.Append(", timestamp TIMESTAMP");

                // Define history PRIMARY KEY (always sorts by timestamp)
                sb.Append($", PRIMARY KEY (({string.Join(", ", primaryKey.Keys)}), timestamp)");
                sb.Append(") WITH CLUSTERING ORDER BY (timestamp DESC);");

                await _session.ExecuteAsync(new SimpleStatement(sb.ToString()));

                // Create Indexes on Primary Keys for Fast Lookups
                foreach (var key in primaryKey.Keys)
                {
                    var indexQuery = $@"CREATE INDEX IF NOT EXISTS ON {actualKeyspace.Name}.{historyTableName} ({key});";
                    await _session.ExecuteAsync(new SimpleStatement(indexQuery));
                }
            }
        }
    }

    /// <summary>
    /// Checks if a table exists in the keyspace.
    /// </summary>
    private async Task<bool> TableExistsAsync(string keyspace, string tableName)
    {
        await ensureConnected();
        var query = $"SELECT table_name FROM system_schema.tables WHERE keyspace_name = '{keyspace}' AND table_name = '{tableName}';";
        var result = await _session!.ExecuteAsync(new SimpleStatement(query));
        return result.Any();
    }

    public async Task ChangeKeyspaceAsync(string keyspace)
    {
        await ensureConnected();
        var query = $"USE {keyspace};";
        await _session!.ExecuteAsync(new SimpleStatement(query));
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
                await HealthCheckAsync();
                await Task.Delay(TimeSpan.FromSeconds(seconds), _healthCheckCts.Token);
            }
        });
    }


    private async Task HealthCheckAsync()
    {
        if (!await _pingLock.WaitAsync(0)) return;
        try
        {
            if (_session != null)
            {
                await _session.ExecuteAsync(new SimpleStatement("SELECT now() FROM system.local"));
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


    private void StopHealthChecks()
    {
        _healthCheckCts.Cancel();
    }
    #endregion
}


public class ScyllaVaultFactory : DatabaseVaultFactory
{
    public ScyllaVaultFactory(IScyllaDbProvider databaseProvider) : base(databaseProvider)
    {
    }
}