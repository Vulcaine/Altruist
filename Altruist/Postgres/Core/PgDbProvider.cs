// Altruist.Postgres/SqlDbProvider.cs
/*
Copyright 2025 Aron Gere

Licensed under the Apache License, Version 2.0 (the "License");
you may not use this file except in compliance with the License.
You may obtain a copy of the License at

    http://www.apache.org/licenses/LICENSE-2.0
*/

using System.Data;
using System.Reflection;
using System.Text;
using System.Text.Json;

using Altruist.Contracts;

using Npgsql;

using NpgsqlTypes;

namespace Altruist.Persistence.Postgres;

[Service(typeof(ISqlDatabaseProvider))]
[Service(typeof(IGeneralDatabaseProvider))]
[ConditionalOnConfig("altruist:persistence:database:provider", havingValue: "postgres")]
public class SqlDbProvider : ISqlDatabaseProvider
{
    private NpgsqlConnection? _conn;

    // Separate config entries (built into a connection string internally)
    private readonly string _host;
    private readonly int _port;
    private readonly string _username;
    private readonly string _password;  // keep original casing
    private readonly string _database;

    // Optional tuning flags
    private readonly bool _pooling;
    private readonly string _sslModeRaw;             // lowercased raw input preserved for parse
    private readonly bool _trustServerCertificate;   // useful for local dev if SSL is enabled

    // ---------- helpers ----------

    private static string NormLower(string? s) => (s ?? string.Empty).Trim().ToLowerInvariant();

    private static Npgsql.SslMode ParseSslMode(string rawLower)
    {
        // Accept common variants, all lowercased
        return rawLower switch
        {
            "" or "disable" => Npgsql.SslMode.Disable,
            "allow" => Npgsql.SslMode.Allow,
            "prefer" => Npgsql.SslMode.Prefer,
            "require" => Npgsql.SslMode.Require,
            "verifyca" or "verify-ca" => Npgsql.SslMode.VerifyCA,
            "verifyfull" or "verify-full" => Npgsql.SslMode.VerifyFull,
            _ => Npgsql.SslMode.Disable
        };
    }

    private string BuildConnectionString(string? overrideHost = null, int? overridePort = null)
    {
        var csb = new NpgsqlConnectionStringBuilder
        {
            Host = overrideHost ?? _host,
            Port = overridePort ?? _port,
            Username = _username,
            Password = _password,     // do NOT lowercase passwords
            Database = _database,
            Pooling = _pooling,
            SslMode = ParseSslMode(_sslModeRaw)
        };

        return csb.ConnectionString;
    }

    public string GetConnectionString() => BuildConnectionString();

    public bool IsConnected { get; private set; }

    public string ServiceName { get; } = "PostgreSQL";
    public IDatabaseServiceToken Token { get; private set; } = PostgresDBToken.Instance;

    public event Action? OnConnected;
    public event Action<Exception>? OnFailed;
    public event Action<Exception>? OnRetryExhausted;

    public SqlDbProvider(
        [AppConfigValue("altruist:persistence:database:host")] string host,
        [AppConfigValue("altruist:persistence:database:port", "5432")] int port,
        [AppConfigValue("altruist:persistence:database:username")] string username,
        [AppConfigValue("altruist:persistence:database:password")] string password,
        [AppConfigValue("altruist:persistence:database:database")] string database,
        // optional knobs (all defaults match local dev)
        [AppConfigValue("altruist:persistence:database:pooling", "true")] bool pooling = true,
        [AppConfigValue("altruist:persistence:database:ssl-mode", "disable")] string sslMode = "disable",
        [AppConfigValue("altruist:persistence:database:trust-server-certificate", "false")] bool trustServerCertificate = false
    )
    {
        // Normalize all string inputs to lowercase first (as requested).
        // NOTE: Password is intentionally NOT lowercased.
        var hostLower = NormLower(host);
        var userLower = NormLower(username);
        var dbLower = NormLower(database);
        var sslLower = NormLower(sslMode);

        _host = string.IsNullOrWhiteSpace(hostLower) ? "localhost" : hostLower;
        _port = port <= 0 ? 5432 : port;

        // For PostgreSQL, unquoted identifiers fold to lower-case; normalizing user/db helps avoid surprises.
        _username = string.IsNullOrWhiteSpace(userLower) ? throw new ArgumentNullException(nameof(username)) : userLower;
        _database = string.IsNullOrWhiteSpace(dbLower) ? throw new ArgumentNullException(nameof(database)) : dbLower;

        _password = password ?? throw new ArgumentNullException(nameof(password)); // keep original casing
        _pooling = pooling;
        _sslModeRaw = sslLower; // store lowercased raw; we map to enum later
        _trustServerCertificate = trustServerCertificate;
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
                _conn = new NpgsqlConnection(BuildConnectionString());
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

    // IConnectable-style overload: protocol/host/port-based connect with retries
    public async Task ConnectAsync(string protocol, string host, int port, int maxRetries, int delayMilliseconds)
    {
        Exception? last = null;

        // Normalize override host to lowercase as well
        var hostLower = NormLower(host);

        for (int attempt = 1; attempt <= maxRetries; attempt++)
        {
            try
            {
                // Protocol is ignored by Npgsql; SSL is controlled by SslMode.
                _conn = new NpgsqlConnection(BuildConnectionString(overrideHost: hostLower, overridePort: port));
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
        cmd.CommandText = $"SET search_path TO \"{NormLower(schema)}\";";
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
        catch (Exception)
        {
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
        catch (Exception)
        {
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
    /// Translates '?' placeholders to PostgreSQL '@p1..@pn' and binds parameters.
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
            var value = parameters[i];
            var p = cmd.CreateParameter();
            p.ParameterName = $"@p{i + 1}";

            if (value is null)
            {
                p.Value = DBNull.Value;
            }
            else
            {
                var type = value.GetType();

                if (type.IsGenericType &&
                    type.GetGenericTypeDefinition() == typeof(Dictionary<,>))
                {
                    var args = type.GetGenericArguments();
                    if (args[0] == typeof(string) && args[1] == typeof(string))
                    {
                        p.NpgsqlDbType = NpgsqlDbType.Jsonb;
                        p.Value = JsonSerializer.Serialize(value);
                    }
                    else
                    {
                        p.Value = value;
                    }
                }
                else
                {
                    p.Value = value;
                }
            }

            cmd.Parameters.Add(p);
        }

        return cmd;
    }

    private static string ReplaceQuestionMarks(string sql, int expectedCount)
    {
        var sb = new StringBuilder(sql.Length + expectedCount * 3);
        bool inSingle = false; // 'string'
        bool inDouble = false; // "identifier"
        int paramIndex = 0;

        for (int i = 0; i < sql.Length; i++)
        {
            char c = sql[i];

            if (!inDouble && c == '\'')
            {
                sb.Append(c);
                // handle escaped single quote ''
                if (i + 1 < sql.Length && sql[i + 1] == '\'')
                {
                    sb.Append(sql[i + 1]);
                    i++;
                }
                else
                {
                    inSingle = !inSingle;
                }
                continue;
            }

            if (!inSingle && c == '"')
            {
                sb.Append(c);
                inDouble = !inDouble;
                continue;
            }

            if (!inSingle && !inDouble && c == '?')
            {
                paramIndex++;
                sb.Append("@p").Append(paramIndex);
                continue;
            }

            sb.Append(c);
        }

        return sb.ToString();
    }

    #endregion

    #region Schema creation (no table DDL here)

    public Task CreateKeySpaceAsync(string keyspace, ReplicationOptions? options = null)
        => CreateSchemaAsync(keyspace, options);

    public async Task CreateSchemaAsync(string schema, ReplicationOptions? _ = null)
    {
        await EnsureConnectedAsync();
        using var cmd = _conn!.CreateCommand();
        cmd.CommandText = $"CREATE SCHEMA IF NOT EXISTS \"{NormLower(schema)}\";";
        await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
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
            // Use a separate, short-lived connection for the health check to avoid
            // "command already in progress" on the shared _conn.
            await using var pingConn = new NpgsqlConnection(BuildConnectionString());
            await pingConn.OpenAsync().ConfigureAwait(false);

            await using var cmd = pingConn.CreateCommand();
            cmd.CommandText = "SELECT 1";
            cmd.CommandTimeout = 3;

            await cmd.ExecuteScalarAsync().ConfigureAwait(false);

            if (!IsConnected)
            {
                IsConnected = true;
                OnConnected?.Invoke();
            }
        }
        catch (Npgsql.NpgsqlOperationInProgressException)
        {
            // If the pool/connection is busy for some reason, just skip this tick.
            // We don't want health checks to interfere with normal operations.
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

