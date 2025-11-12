/* 
Copyright 2025 Aron Gere

Licensed under the Apache License, Version 2.0 (the "License");
You may obtain a copy of the License at
http://www.apache.org/licenses/LICENSE-2.0
*/

using System.Data;
using System.Reflection;
using System.Text;

using Altruist.Contracts;
using Altruist.Persistence;
using Altruist.UORM;

using Npgsql;

namespace Altruist.Postgres;

[Service(typeof(ISqlDatabaseProvider))]
[Service(typeof(IGeneralDatabaseProvider))]
[ConditionalOnConfig("altruist:persistence:database:provider", havingValue: "postgres")]
public class SqlDbProvider : ISqlDatabaseProvider
{
    private NpgsqlConnection? _conn;
    private readonly string _connectionString;
    public bool IsConnected { get; private set; }

    public string ServiceName { get; } = "PostgreSQL";
    public IDatabaseServiceToken Token { get; private set; } = PostgresDBToken.Instance;

    public event Action? OnConnected;
    public event Action<Exception>? OnFailed;
    public event Action<Exception>? OnRetryExhausted;

    public SqlDbProvider(
        [AppConfigValue("altruist:persistence:database:connection-string")] string connectionString)
    {
        _connectionString = connectionString ?? throw new ArgumentNullException(nameof(connectionString));
    }

    #region Connection lifecycle

    private async Task EnsureConnectedAsync()
    {
        if (!IsConnected)
            await ConnectAsync();
    }

    public async Task ConnectAsync(int maxRetries = 30, int delayMilliseconds = 2000)
    {
        Exception? last = null;

        for (int attempt = 1; attempt <= maxRetries; attempt++)
        {
            try
            {
                _conn = new NpgsqlConnection(_connectionString);
                await _conn.OpenAsync().ConfigureAwait(false);
                RaiseConnectedEvent();
                StartHealthChecks();
                return;
            }
            catch (Exception ex)
            {
                if (attempt == 1)
                    RaiseFailedEvent(ex);
                last = ex;
                IsConnected = false;
                if (attempt < maxRetries)
                    await Task.Delay(delayMilliseconds).ConfigureAwait(false);
            }
        }

        RaiseOnRetryExhaustedEvent(last!);
    }

    // IConnectable overload: protocol/host/port-based connect with retries
    public async Task ConnectAsync(string protocol, string host, int port, int maxRetries, int delayMilliseconds)
    {
        Exception? last = null;

        for (int attempt = 1; attempt <= maxRetries; attempt++)
        {
            try
            {
                var csb = new NpgsqlConnectionStringBuilder(_connectionString)
                {
                    Host = host,
                    Port = port
                    // protocol is ignored by Npgsql; TLS etc. is controlled via SSL/TLS settings in the conn string
                };

                _conn = new NpgsqlConnection(csb.ConnectionString);
                await _conn.OpenAsync().ConfigureAwait(false);
                RaiseConnectedEvent();
                StartHealthChecks();
                return;
            }
            catch (Exception ex)
            {
                if (attempt == 1)
                    RaiseFailedEvent(ex);
                last = ex;
                IsConnected = false;
                if (attempt < maxRetries)
                    await Task.Delay(delayMilliseconds).ConfigureAwait(false);
            }
        }

        RaiseOnRetryExhaustedEvent(last!);
    }

    public Task ConnectAsync() => ConnectAsync(30, 2000);

    public async Task ShutdownAsync(Exception? ex = null)
    {
        StopHealthChecks();
        if (_conn is null)
            return;
        try
        {
            await _conn.CloseAsync().ConfigureAwait(false);
        }
        finally
        {
            await _conn.DisposeAsync().ConfigureAwait(false);
            _conn = null;
            IsConnected = false;
        }
    }

    public async Task ChangeKeyspaceAsync(string schema)
    {
        await EnsureConnectedAsync();
        using var cmd = _conn!.CreateCommand();
        cmd.CommandText = $"SET search_path TO \"{schema}\";";
        await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
    }

    // IConnectable event-raiser implementations
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

    #endregion

    #region Provider API (Query / Execute)

    public async Task<IEnumerable<TVaultModel>> QueryAsync<TVaultModel>(string sql, List<object>? parameters = null)
        where TVaultModel : class, IVaultModel
        => (await ExecuteFetchAsync<TVaultModel>(sql, parameters).ConfigureAwait(false)).ToList();

    public async Task<TVaultModel?> QuerySingleAsync<TVaultModel>(string sql, List<object>? parameters = null)
        where TVaultModel : class, IVaultModel
        => (await ExecuteFetchAsync<TVaultModel>(sql, parameters).ConfigureAwait(false)).FirstOrDefault();

    public async Task<long> ExecuteCountAsync(string sql, List<object>? parameters = null)
    {
        try
        {
            await EnsureConnectedAsync();
            using var cmd = PrepareCommand(_conn!, sql, parameters);
            var obj = await cmd.ExecuteScalarAsync().ConfigureAwait(false);
            if (obj is long l)
                return l;
            if (obj is int i)
                return i;
            if (obj is decimal d)
                return (long)d;
            return obj is null ? 0 : Convert.ToInt64(obj);
        }
        catch (Exception ex)
        {
            RaiseFailedEvent(ex);
            throw;
        }
    }

    /// <summary>Executes INSERT/UPDATE/DELETE or batched statements; returns affected rows (driver-dependent).</summary>
    public async Task<long> ExecuteAsync(string sql, List<object>? parameters = null)
    {
        try
        {
            await EnsureConnectedAsync();
            using var cmd = PrepareCommand(_conn!, sql, parameters);
            var affected = await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
            if (!IsConnected)
                RaiseConnectedEvent();
            return affected;
        }
        catch (Exception ex)
        {
            RaiseFailedEvent(ex);
            throw;
        }
    }

    public async Task<long> UpdateAsync<TVaultModel>(TVaultModel entity)
        where TVaultModel : class, IVaultModel
    {
        // No POCO-mapper update in this provider (parity with CQL provider surface)
        await Task.CompletedTask;
        return 1;
    }

    public async Task<long> DeleteAsync<TVaultModel>(TVaultModel entity)
        where TVaultModel : class, IVaultModel
    {
        await Task.CompletedTask;
        return 1;
    }

    private async Task<IEnumerable<TVaultModel>> ExecuteFetchAsync<TVaultModel>(string sql, List<object>? parameters)
        where TVaultModel : class, IVaultModel
    {
        try
        {
            await EnsureConnectedAsync();
            using var cmd = PrepareCommand(_conn!, sql, parameters);
            using var reader = await cmd.ExecuteReaderAsync(CommandBehavior.SequentialAccess).ConfigureAwait(false);
            var list = new List<TVaultModel>();
            var type = typeof(TVaultModel);

            var props = type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                            .Where(p => p.CanWrite).ToArray();

            var ordinals = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < reader.FieldCount; i++)
                ordinals[reader.GetName(i)] = i;

            while (await reader.ReadAsync().ConfigureAwait(false))
            {
                var inst = Activator.CreateInstance<TVaultModel>();
                foreach (var p in props)
                {
                    if (!ordinals.TryGetValue(p.Name, out var ord))
                        continue;
                    var val = await reader.IsDBNullAsync(ord).ConfigureAwait(false)
                        ? null
                        : reader.GetValue(ord);
                    if (val is not null)
                    {
                        var targetType = Nullable.GetUnderlyingType(p.PropertyType) ?? p.PropertyType;
                        var converted = ConvertValue(val, targetType);
                        p.SetValue(inst, converted);
                    }
                }
                list.Add(inst);
            }
            return list;
        }
        catch (Exception ex)
        {
            RaiseFailedEvent(ex);
            throw;
        }
        finally
        {
            if (IsConnected)
                OnConnected?.Invoke();
        }
    }

    private static object? ConvertValue(object val, Type targetType)
    {
        if (targetType.IsAssignableFrom(val.GetType()))
            return val;
        if (targetType.IsEnum)
            return Enum.Parse(targetType, val.ToString()!, ignoreCase: true);
        if (targetType == typeof(Guid))
            return val switch { Guid g => g, string s => Guid.Parse(s), _ => new Guid((byte[])val) };
        if (targetType == typeof(DateTime))
            return Convert.ToDateTime(val, System.Globalization.CultureInfo.InvariantCulture);
        return Convert.ChangeType(val, targetType);
    }

    /// <summary>
    /// Translates '?' placeholders to PostgreSQL '$1..$n' and binds parameters.
    /// Handles single-quoted string literals to avoid replacing '?' inside them.
    /// </summary>
    private static NpgsqlCommand PrepareCommand(NpgsqlConnection conn, string sql, List<object>? parameters)
    {
        var cmd = conn.CreateCommand();

        if (parameters is null || parameters.Count == 0)
        {
            cmd.CommandText = sql;
            return cmd;
        }

        cmd.CommandText = ReplaceQuestionMarks(sql, parameters.Count);
        for (int i = 0; i < parameters.Count; i++)
        {
            var p = cmd.CreateParameter();
            p.ParameterName = $"@p{i + 1}";
            p.Value = parameters[i] ?? DBNull.Value;
            cmd.Parameters.Add(p);
        }

        return cmd;
    }

    private static string ReplaceQuestionMarks(string sql, int count)
    {
        var sb = new StringBuilder(sql.Length + count * 2);
        bool inSingle = false;

        for (int i = 0, paramIndex = 0; i < sql.Length; i++)
        {
            char c = sql[i];

            if (c == '\'')
            {
                sb.Append(c);
                if (i + 1 < sql.Length && sql[i + 1] == '\'')
                {
                    i++;
                    sb.Append('\'');
                }
                else
                {
                    inSingle = !inSingle;
                }
                continue;
            }

            if (!inSingle && c == '?')
            {
                paramIndex++;
                sb.Append('$').Append(paramIndex.ToString());
                continue;
            }

            sb.Append(c);
        }

        return sb.ToString();
    }

    #endregion

    #region Schema / Table creation

    // IGeneralDatabaseProvider requires CreateKeySpaceAsync (Scylla naming); map to PG schema creation
    public Task CreateKeySpaceAsync(string keyspace, ReplicationOptions? options = null)
        => CreateSchemaAsync(keyspace, options);

    public async Task CreateSchemaAsync(string schema, ReplicationOptions? _ = null)
    {
        await EnsureConnectedAsync();
        using var cmd = _conn!.CreateCommand();
        cmd.CommandText = $"CREATE SCHEMA IF NOT EXISTS \"{schema}\";";
        await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
    }

    public async Task CreateTableAsync<TVaultModel>(IKeyspace? schema = null) where TVaultModel : class, IVaultModel
        => await CreateTableAsync(typeof(TVaultModel), schema).ConfigureAwait(false);

    public async Task CreateTableAsync(Type entityType, IKeyspace? schema = null)
    {
        await EnsureConnectedAsync();

        var document = Document.From(entityType);
        var tableAttr = entityType.GetCustomAttribute<VaultAttribute>()
                       ?? throw new InvalidOperationException($"Type '{entityType.Name}' is missing VaultAttribute.");

        string schemaName = (schema?.Name) ?? "public";
        string tableName = $"{QuoteIdent(schemaName)}.{QuoteIdent(document.Name)}";
        bool storeHistory = tableAttr.StoreHistory;

        var keyColumns = ReflectionUtils.GetPrimaryKeyColumns(entityType);
        if (keyColumns.Count == 0)
            throw new InvalidOperationException($"PrimaryKeyAttribute is required on '{entityType.Name}' or its base types.");

        var sortingAttr = document.SortingBy;
        var sortingKey = ReflectionUtils.ResolveSortingColumnName(document, entityType);
        bool sortAscending = sortingAttr?.Ascending ?? true;

        var mappableProps = ReflectionUtils.GetMappableProperties(entityType);
        var columns = mappableProps.Select(p =>
        {
            var colName = ReflectionUtils.GetColumnName(p);
            var sqlType = MapTypeToSql(p.PropertyType);
            return $"{QuoteIdent(colName)} {sqlType}";
        });

        var sb = new StringBuilder();
        sb.Append($"CREATE TABLE IF NOT EXISTS {tableName} (");
        sb.Append(string.Join(", ", columns));
        sb.Append(", PRIMARY KEY (");
        if (sortingKey is not null)
        {
            sb.Append(string.Join(", ", keyColumns.Select(QuoteIdent)));
            sb.Append(", ");
            sb.Append(QuoteIdent(sortingKey));
        }
        else
        {
            sb.Append(string.Join(", ", keyColumns.Select(QuoteIdent)));
        }
        sb.Append("));");

        using (var cmd = _conn!.CreateCommand())
        {
            cmd.CommandText = sb.ToString();
            await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
        }

        if (sortingKey is not null && !keyColumns.Contains(sortingKey, StringComparer.OrdinalIgnoreCase))
        {
            var idx = $"{document.Name}_{sortingKey}_idx";
            using var idxCmd = _conn!.CreateCommand();
            idxCmd.CommandText = $"CREATE INDEX IF NOT EXISTS {QuoteIdent(idx)} ON {tableName} ({QuoteIdent(sortingKey)} {(sortAscending ? "" : "DESC")});";
            await idxCmd.ExecuteNonQueryAsync().ConfigureAwait(false);
        }

        if (document.Indexes is not null && document.Indexes.Count > 0)
        {
            var pkSet = new HashSet<string>(keyColumns, StringComparer.OrdinalIgnoreCase);
            foreach (var idxCol in document.Indexes.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (pkSet.Contains(idxCol))
                    continue;
                var indexName = $"{document.Name}_{idxCol}_idx";
                using var idxCmd = _conn!.CreateCommand();
                idxCmd.CommandText = $"CREATE INDEX IF NOT EXISTS {QuoteIdent(indexName)} ON {tableName} ({QuoteIdent(idxCol)});";
                await idxCmd.ExecuteNonQueryAsync().ConfigureAwait(false);
            }
        }

        if (storeHistory)
        {
            string history = $"{QuoteIdent(schemaName)}.{QuoteIdent(document.Name + "_history")}";

            var histCols = mappableProps.Select(p =>
            {
                var colName = ReflectionUtils.GetColumnName(p);
                var sqlType = MapTypeToSql(p.PropertyType);
                return $"{QuoteIdent(colName)} {sqlType}";
            });

            var hist = new StringBuilder();
            hist.Append($"CREATE TABLE IF NOT EXISTS {history} (");
            hist.Append(string.Join(", ", histCols));
            hist.Append(", \"timestamp\" timestamptz NOT NULL, ");
            hist.Append("PRIMARY KEY (");
            hist.Append(string.Join(", ", keyColumns.Select(QuoteIdent)));
            hist.Append(", \"timestamp\"));");

            using (var cmd = _conn!.CreateCommand())
            {
                cmd.CommandText = hist.ToString();
                await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
            }

            foreach (var key in keyColumns)
            {
                using var idxCmd = _conn!.CreateCommand();
                var idxName = $"{document.Name}_history_{key}_idx";
                idxCmd.CommandText = $"CREATE INDEX IF NOT EXISTS {QuoteIdent(idxName)} ON {history} ({QuoteIdent(key)});";
                await idxCmd.ExecuteNonQueryAsync().ConfigureAwait(false);
            }
        }
    }

    private static string QuoteIdent(string ident) => $"\"{ident.Replace("\"", "\"\"")}\"";

    private static string MapTypeToSql(Type type)
    {
        if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Nullable<>))
            type = Nullable.GetUnderlyingType(type)!;

        if (type == typeof(string))
            return "text";
        if (type == typeof(bool))
            return "boolean";
        if (type == typeof(byte))
            return "smallint";
        if (type == typeof(short))
            return "smallint";
        if (type == typeof(int))
            return "integer";
        if (type == typeof(long))
            return "bigint";
        if (type == typeof(float))
            return "real";
        if (type == typeof(double))
            return "double precision";
        if (type == typeof(decimal))
            return "numeric";
        if (type == typeof(DateTime))
            return "timestamp";
        if (type == typeof(DateTimeOffset))
            return "timestamptz";
        if (type == typeof(Guid))
            return "uuid";
        if (type == typeof(byte[]))
            return "bytea";
        if (type == typeof(TimeSpan))
            return "interval";

        if (type.IsArray)
            return "jsonb";
        if (typeof(System.Collections.IEnumerable).IsAssignableFrom(type) && type != typeof(string))
            return "jsonb";

        if (type.IsEnum)
            return "text";
        return "jsonb";
    }

    #endregion

    #region Health checks

    private CancellationTokenSource _healthCts = new();
    private SemaphoreSlim _pingLock = new(1, 1);

    private void StartHealthChecks(int seconds = 5)
    {
        _ = Task.Run(async () =>
        {
            while (!_healthCts.Token.IsCancellationRequested)
            {
                try
                {
                    await HealthCheckAsync();
                    await Task.Delay(TimeSpan.FromSeconds(seconds), _healthCts.Token);
                }
                catch (OperationCanceledException) { /* ignore */ }
            }
        });
    }

    private async Task HealthCheckAsync()
    {
        if (!await _pingLock.WaitAsync(0))
            return;
        try
        {
            if (_conn != null)
            {
                using var cmd = _conn.CreateCommand();
                cmd.CommandText = "SELECT 1";
                await cmd.ExecuteScalarAsync().ConfigureAwait(false);
                if (!IsConnected)
                {
                    IsConnected = true;
                    OnConnected?.Invoke();
                }
            }
        }
        catch (Exception ex)
        {
            if (IsConnected)
            {
                IsConnected = false;
                OnFailed?.Invoke(ex);
            }
        }
        finally
        {
            _pingLock.Release();
        }
    }

    private void StopHealthChecks() => _healthCts.Cancel();

    #endregion
}

[Service(typeof(IDatabaseVaultFactory))]
[Service(typeof(DatabaseVaultFactory))]
public class SqlVaultFactory : DatabaseVaultFactory
{
    public SqlVaultFactory(ISqlDatabaseProvider databaseProvider, IServiceProvider serviceProvider)
        : base(databaseProvider, serviceProvider) { }
}
