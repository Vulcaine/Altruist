using System.Reflection;
using System.Text;
using Altruist.Contracts;
using Altruist.Database;
using Cassandra;
using Cassandra.Mapping;
using Altruist.UORM;
using Microsoft.AspNetCore.Razor.TagHelpers;
using System.Threading.Tasks;

namespace Altruist.ScyllaDB;

public enum ReplicationStrategy
{
    SimpleStrategy,
    NetworkTopologyStrategy
}


public class ScyllaReplicationOptions : ReplicationOptions
{
    public ReplicationStrategy Strategy { get; set; } = ReplicationStrategy.SimpleStrategy;
}

public interface IScyllaKeyspace : IKeyspace
{
    ScyllaReplicationOptions? Options { get; set; }
}

public abstract class ScyllaKeyspace : IScyllaKeyspace
{
    public string Name { get; set; } = "altruist";
    public ScyllaReplicationOptions? Options { get; set; } = new ScyllaReplicationOptions();
}

public class DefaultScyllaKeyspace : ScyllaKeyspace
{
}

public interface IScyllaDbProvider : ICqlDatabaseProvider
{
    Task ConnectAsync();
}

public class ScyllaDbProvider : IScyllaDbProvider
{
    private ISession? _session { get; set; }
    private IMapper? _mapper { get; set; }
    private readonly List<string> _contactPoints;

    public IDatabaseServiceToken Token { get; private set; }

    public ScyllaDbProvider(List<string> contactPoints)
    {
        _contactPoints = contactPoints;
        Token = ScyllaDBToken.Instance;
    }

    private async Task ensureConnected()
    {
        if (_session == null)
        {
            await ConnectAsync();
        }
    }

    private bool IsConnected => _session != null;

    public Task ConnectAsync()
    {
        if (_contactPoints.Count == 0 || IsConnected)
        {
            return Task.CompletedTask;
        }

        var clusterBuilder = Cluster.Builder().WithDefaultKeyspace("altruist");

        foreach (var contactPoint in _contactPoints)
        {
            // Check if the contactPoint has a scheme, else prepend 'cql://'
            string contactPointWithScheme = contactPoint.Contains("://") ? contactPoint : "cql://" + contactPoint;
            var uri = new Uri(contactPointWithScheme);

            string host = uri.Host;
            int port = uri.Port;

            clusterBuilder = clusterBuilder.AddContactPoint(host).WithPort(port);
        }

        var cluster = clusterBuilder.Build();
        _session = cluster.ConnectAndCreateDefaultKeyspaceIfNotExists();
        _mapper = new Mapper(_session);
        return Task.CompletedTask;
    }

    public ScyllaDbProvider(ISession session, IMapper mapper, IDatabaseServiceToken token)
    {
        _contactPoints = new List<string>();
        _session = session;
        _mapper = mapper;
        Token = token;
    }

    public async Task<IEnumerable<TVaultModel>> QueryAsync<TVaultModel>(string cqlQuery, List<object> parameters) where TVaultModel : class, IVaultModel
    {
        await ensureConnected();
        if (parameters.Count == 0)
        {
            return (await _mapper!.FetchAsync<TVaultModel>(cqlQuery)).ToList();
        }
        else
        {
            return (await _mapper!.FetchAsync<TVaultModel>(cqlQuery, parameters)).ToList();
        }
    }

    public async Task<TVaultModel?> QuerySingleAsync<TVaultModel>(string cqlQuery, List<object> parameters) where TVaultModel : class, IVaultModel
    {
        await ensureConnected();
        if (parameters.Count == 0)
        {
            return (await _mapper!.FetchAsync<TVaultModel>(cqlQuery)).FirstOrDefault();
        }
        else
        {
            return (await _mapper!.FetchAsync<TVaultModel>(cqlQuery, parameters)).FirstOrDefault();
        }
    }

    public async Task<int> ExecuteAsync(string cqlQuery, List<object> parameters)
    {
        await ensureConnected();
        var statement = _session!.Prepare(cqlQuery).Bind(parameters.Count == 0 ? null : parameters);
        var result = await _session.ExecuteAsync(statement);
        return result?.Info?.AchievedConsistency != null ? result.GetRows().Count() : 0;
    }

    public async Task<int> UpdateAsync<TVaultModel>(TVaultModel entity) where TVaultModel : class, IVaultModel
    {
        await ensureConnected();
        await _mapper!.UpdateAsync(entity);
        return 1;
    }

    public async Task<int> DeleteAsync<TVaultModel>(TVaultModel entity) where TVaultModel : class, IVaultModel
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

        string tableName = tableAttribute.Name;
        bool storeHistory = tableAttribute.StoreHistory;

        var primaryKeyAttr = entityType.GetCustomAttribute<VaultPrimaryKeyAttribute>();
        if (primaryKeyAttr == null || primaryKeyAttr.Keys.Length == 0)
        {
            throw new InvalidOperationException($"PrimaryKeyAttribute is required on '{entityType.Name}'.");
        }

        // === Get Sorting Key from Class-Level Attribute ===
        var sortingAttribute = entityType.GetCustomAttribute<VaultSortingByAttribute>();
        string? sortingKey = sortingAttribute?.Name.ToLower();
        bool sortAscending = sortingAttribute?.Ascending ?? true;

        // Validate Sorting Key (if defined)
        if (sortingKey != null && !entityType.GetProperties().Any(p => p.Name.ToLower() == sortingKey))
        {
            throw new InvalidOperationException($"Sorting key '{sortingKey}' is not a property of '{entityType.Name}'.");
        }

        // === Define Table Columns ===
        var columns = entityType.GetProperties()
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
        sb.AppendLine($"CREATE TABLE IF NOT EXISTS {actualKeyspace.Name}.{tableName} (");
        sb.AppendLine(string.Join(", ", columns));

        // Define PRIMARY KEY (if sorting is needed)
        if (sortingKey != null)
        {
            sb.AppendLine($", PRIMARY KEY (({string.Join(", ", primaryKeyAttr.Keys)}), {sortingKey})");
            sb.AppendLine($") WITH CLUSTERING ORDER BY ({sortingKey} {(sortAscending ? "ASC" : "DESC")});");
        }
        else
        {
            sb.AppendLine($", PRIMARY KEY ({string.Join(", ", primaryKeyAttr.Keys)}) );");
        }

        await _session!.ExecuteAsync(new SimpleStatement(sb.ToString()));

        // === STEP 2: CREATE HISTORY TABLE (if enabled) ===
        if (storeHistory)
        {
            string historyTableName = $"{tableName}_history";

            if (!await TableExistsAsync(actualKeyspace.Name, historyTableName))
            {
                sb.Clear();
                sb.AppendLine($"CREATE TABLE IF NOT EXISTS {actualKeyspace.Name}.{historyTableName} (");
                sb.AppendLine(string.Join(", ", columns));
                sb.AppendLine(", timestamp TIMESTAMP");

                // Define history PRIMARY KEY (always sorts by timestamp)
                sb.AppendLine($", PRIMARY KEY (({string.Join(", ", primaryKeyAttr.Keys)}), timestamp)");
                sb.AppendLine(") WITH CLUSTERING ORDER BY (timestamp DESC);");

                await _session.ExecuteAsync(new SimpleStatement(sb.ToString()));

                // Create Indexes on Primary Keys for Fast Lookups
                foreach (var key in primaryKeyAttr.Keys)
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
}


public class ScyllaVaultFactory : DatabaseVaultFactory
{
    public ScyllaVaultFactory(IScyllaDbProvider databaseProvider) : base(databaseProvider)
    {
    }
}