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
    private readonly string _password;
    private readonly string _database;

    private readonly bool _pooling;
    private readonly string _sslModeRaw;
    private readonly bool _trustServerCertificate;

    private readonly JsonSerializerOptions _jsonOptions;

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
            SslMode = ParseSslMode(_sslModeRaw),
            TrustServerCertificate = _trustServerCertificate
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
        JsonSerializerOptions jsonOptions,
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
        var hostLower = NormLower(host);
        var userLower = NormLower(username);
        var dbLower = NormLower(database);
        var sslLower = NormLower(sslMode);

        _host = string.IsNullOrWhiteSpace(hostLower) ? "localhost" : hostLower;
        _port = port <= 0 ? 5432 : port;

        _username = string.IsNullOrWhiteSpace(userLower) ? throw new ArgumentNullException(nameof(username)) : userLower;
        _database = string.IsNullOrWhiteSpace(dbLower) ? throw new ArgumentNullException(nameof(database)) : dbLower;

        _password = password ?? throw new ArgumentNullException(nameof(password));
        _pooling = pooling;
        _sslModeRaw = sslLower;
        _trustServerCertificate = trustServerCertificate;
        _jsonOptions = jsonOptions;
    }

    #region Connection lifecycle

    private async Task EnsureConnectedAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        IsConnected = _conn?.State == ConnectionState.Open;
        if (!IsConnected)
            await ConnectAsync(30, 2000, ct).ConfigureAwait(false);
    }

    // Backward-compatible signature (older callers / older interface)
    public Task ConnectAsync(int maxRetries, int delayMilliseconds)
        => ConnectAsync(maxRetries, delayMilliseconds, CancellationToken.None);

    // New overload with CancellationToken (preferred)
    public async Task ConnectAsync(int maxRetries, int delayMilliseconds, CancellationToken ct = default)
    {
        Exception? last = null;

        for (int attempt = 1; attempt <= maxRetries; attempt++)
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                _conn = new NpgsqlConnection(BuildConnectionString());
                await _conn.OpenAsync(ct).ConfigureAwait(false);
                RaiseConnectedEvent();
                StartHealthChecks();
                return;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                if (attempt == 1)
                    RaiseFailedEvent(ex);
                last = ex;
                IsConnected = false;

                if (attempt < maxRetries)
                    await Task.Delay(delayMilliseconds, ct).ConfigureAwait(false);
            }
        }

        RaiseOnRetryExhaustedEvent(last!);
    }

    // IConnectable-style overload: protocol/host/port-based connect with retries (existing signature)
    public Task ConnectAsync(string protocol, string host, int port, int maxRetries, int delayMilliseconds)
        => ConnectAsync(protocol, host, port, maxRetries, delayMilliseconds, CancellationToken.None);

    // New overload with cancellation token (doesn't break existing callers)
    public async Task ConnectAsync(
        string protocol,
        string host,
        int port,
        int maxRetries,
        int delayMilliseconds,
        CancellationToken ct = default)
    {
        Exception? last = null;

        // Normalize override host to lowercase as well
        var hostLower = NormLower(host);

        for (int attempt = 1; attempt <= maxRetries; attempt++)
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                // Protocol is ignored by Npgsql; SSL is controlled by SslMode.
                _conn = new NpgsqlConnection(BuildConnectionString(overrideHost: hostLower, overridePort: port));
                await _conn.OpenAsync(ct).ConfigureAwait(false);
                RaiseConnectedEvent();
                StartHealthChecks();
                return;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                if (attempt == 1)
                    RaiseFailedEvent(ex);
                last = ex;
                IsConnected = false;

                if (attempt < maxRetries)
                    await Task.Delay(delayMilliseconds, ct).ConfigureAwait(false);
            }
        }

        RaiseOnRetryExhaustedEvent(last!);
    }

    public Task ConnectAsync() => ConnectAsync(30, 2000, CancellationToken.None);

    // Optional convenience overload
    public Task ConnectAsync(CancellationToken ct) => ConnectAsync(30, 2000, ct);

    // Backward-compatible signature (older callers / older interface)
    public Task ShutdownAsync(Exception? ex = null) => ShutdownAsync(ex, CancellationToken.None);

    public async Task ShutdownAsync(Exception? ex = null, CancellationToken ct = default)
    {
        StopHealthChecks();

        if (_conn is null)
            return;

        try
        {
            // Npgsql CloseAsync may or may not accept a CancellationToken depending on version;
            // keep it compatible.
            await _conn.CloseAsync().ConfigureAwait(false);
        }
        finally
        {
            await _conn.DisposeAsync().ConfigureAwait(false);
            _conn = null;
            IsConnected = false;
        }
    }

    // Backward-compatible signature (older callers / older interface)
    public Task ChangeKeyspaceAsync(string schema) => ChangeKeyspaceAsync(schema, CancellationToken.None);

    public async Task ChangeKeyspaceAsync(string schema, CancellationToken ct = default)
    {
        await EnsureConnectedAsync(ct).ConfigureAwait(false);
        await using var cmd = _conn!.CreateCommand();
        cmd.CommandText = $"SET search_path TO \"{NormLower(schema)}\";";
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
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

    // Backward-compatible signature (older callers / older interface)
    public Task<IEnumerable<TVaultModel>> QueryAsync<TVaultModel>(string sql, List<object>? parameters = null)
        where TVaultModel : class, IVaultModel
        => QueryAsync<TVaultModel>(sql, parameters?.Cast<object?>().ToList(), CancellationToken.None);

    public async Task<IEnumerable<TVaultModel>> QueryAsync<TVaultModel>(
        string sql,
        List<object?>? parameters = null,
        CancellationToken ct = default)
        where TVaultModel : class, IVaultModel
        => (await ExecuteFetchAsync<TVaultModel>(sql, parameters, ct).ConfigureAwait(false)).ToList();

    // Backward-compatible signature (older callers / older interface)
    public Task<TVaultModel?> QuerySingleAsync<TVaultModel>(string sql, List<object>? parameters = null)
        where TVaultModel : class, IVaultModel
        => QuerySingleAsync<TVaultModel>(sql, parameters?.Cast<object?>().ToList(), CancellationToken.None);

    public async Task<TVaultModel?> QuerySingleAsync<TVaultModel>(
        string sql,
        List<object?>? parameters = null,
        CancellationToken ct = default)
        where TVaultModel : class, IVaultModel
        => (await ExecuteFetchAsync<TVaultModel>(sql, parameters, ct).ConfigureAwait(false)).FirstOrDefault();

    // Backward-compatible signature (older callers / older interface)
    public Task<long> ExecuteCountAsync(string sql, List<object>? parameters = null)
        => ExecuteCountAsync(sql, parameters?.Cast<object?>().ToList(), CancellationToken.None);

    public async Task<long> ExecuteCountAsync(
        string sql,
        List<object?>? parameters = null,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        await EnsureConnectedAsync(ct).ConfigureAwait(false);
        await using var cmd = PrepareCommand(_conn!, sql, parameters);

        var obj = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
        if (obj is long l)
            return l;
        if (obj is int i)
            return i;
        if (obj is decimal d)
            return (long)d;
        return obj is null ? 0 : Convert.ToInt64(obj);
    }

    /// <summary>Executes INSERT/UPDATE/DELETE or batched statements; returns affected rows (driver-dependent).</summary>
    // Backward-compatible signature (older callers / older interface)
    public Task<long> ExecuteAsync(string sql, List<object>? parameters = null)
        => ExecuteAsync(sql, parameters?.Cast<object?>().ToList(), CancellationToken.None);

    public async Task<long> ExecuteAsync(
        string sql,
        List<object?>? parameters = null,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        await EnsureConnectedAsync(ct).ConfigureAwait(false);
        await using var cmd = PrepareCommand(_conn!, sql, parameters);

        var affected = await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        if (!IsConnected)
            RaiseConnectedEvent();
        return affected;
    }

    // Backward-compatible signature (older callers / older interface)
    public Task<long> UpdateAsync<TVaultModel>(TVaultModel entity)
        where TVaultModel : class, IVaultModel
        => UpdateAsync(entity, CancellationToken.None);

    public async Task<long> UpdateAsync<TVaultModel>(TVaultModel entity, CancellationToken ct = default)
        where TVaultModel : class, IVaultModel
    {
        // No POCO-mapper update in this provider (parity with CQL provider surface)
        await Task.CompletedTask.ConfigureAwait(false);
        return 1;
    }

    // Backward-compatible signature (older callers / older interface)
    public Task<long> DeleteAsync<TVaultModel>(TVaultModel entity)
        where TVaultModel : class, IVaultModel
        => DeleteAsync(entity, CancellationToken.None);

    public async Task<long> DeleteAsync<TVaultModel>(TVaultModel entity, CancellationToken ct = default)
        where TVaultModel : class, IVaultModel
    {
        await Task.CompletedTask.ConfigureAwait(false);
        return 1;
    }

    private async Task<IEnumerable<TVaultModel>> ExecuteFetchAsync<TVaultModel>(
        string sql,
        List<object?>? parameters,
        CancellationToken ct)
        where TVaultModel : class, IVaultModel
    {
        ct.ThrowIfCancellationRequested();

        try
        {
            await EnsureConnectedAsync(ct).ConfigureAwait(false);

            await using var cmd = PrepareCommand(_conn!, sql, parameters);

            // SequentialAccess is fine, but read in ordinal order.
            await using var reader = await cmd
                .ExecuteReaderAsync(CommandBehavior.SequentialAccess, ct)
                .ConfigureAwait(false);

            var list = new List<TVaultModel>();
            var type = typeof(TVaultModel);

            var props = type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                            .Where(p => p.CanWrite)
                            .ToArray();

            // Map column name -> ordinal
            var ordinals = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < reader.FieldCount; i++)
                ordinals[reader.GetName(i)] = i;

            // Build property bindings and SORT by ordinal (critical for SequentialAccess)
            var bindings = props
                .Select(p => (Prop: p, HasOrd: ordinals.TryGetValue(p.Name, out var o), Ord: o))
                .Where(x => x.HasOrd)
                .OrderBy(x => x.Ord)
                .ToArray();

            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                ct.ThrowIfCancellationRequested();

                var inst = Activator.CreateInstance<TVaultModel>();

                foreach (var b in bindings)
                {
                    ct.ThrowIfCancellationRequested();

                    var p = b.Prop;
                    var ord = b.Ord;

                    if (await reader.IsDBNullAsync(ord, ct).ConfigureAwait(false))
                        continue;

                    var val = reader.GetValue(ord);
                    if (val is null)
                        continue;

                    var targetType = Nullable.GetUnderlyingType(p.PropertyType) ?? p.PropertyType;
                    var converted = ConvertValue(val, targetType);
                    p.SetValue(inst, converted);
                }

                list.Add(inst);
            }

            return list;
        }
        finally
        {
            // Preserve previous behavior
            if (IsConnected)
                OnConnected?.Invoke();
        }
    }

    private object? ConvertValue(object val, Type targetType)
    {
        if (val is null)
            return null;

        var valType = val.GetType();
        if (targetType.IsAssignableFrom(valType))
            return val;

        if (targetType.IsEnum)
            return val is string s
                ? Enum.Parse(targetType, s, ignoreCase: true)
                : Enum.ToObject(targetType, Convert.ToInt32(val));

        if (TryDeserializeJson(val, targetType, out var jsonObj))
            return jsonObj;

        if (targetType == typeof(Guid))
            return val switch
            {
                Guid g => g,
                string s => Guid.Parse(s),
                byte[] b => new Guid(b),
                _ => Guid.Parse(val.ToString()!)
            };

        if (targetType == typeof(DateTime))
            return Convert.ToDateTime(val, System.Globalization.CultureInfo.InvariantCulture);

        return Convert.ChangeType(val, targetType);
    }

    private bool TryDeserializeJson(object val, Type targetType, out object? result)
    {
        result = null;

        if (targetType == typeof(string))
            return false;

        if (!IsJsonTargetType(targetType))
            return false;

        result = val switch
        {
            string json => JsonSerializer.Deserialize(json, targetType, _jsonOptions),
            JsonDocument doc => doc.Deserialize(targetType, _jsonOptions),
            _ => null
        };

        return result is not null;
    }

    private static bool IsJsonTargetType(Type t)
    {
        // Any IDictionary
        if (typeof(System.Collections.IDictionary).IsAssignableFrom(t))
            return true;

        // Any IEnumerable except string (lists/arrays/etc.)
        if (typeof(System.Collections.IEnumerable).IsAssignableFrom(t))
            return true;

        // Any POCO-ish type (not a primitive-ish scalar)
        return !t.IsPrimitive
               && t != typeof(decimal)
               && t != typeof(Guid)
               && t != typeof(DateTime)
               && t != typeof(DateTimeOffset)
               && t != typeof(TimeSpan);
    }

    private static bool ShouldWriteAsJsonb(Type type)
    {
        if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Nullable<>))
            type = Nullable.GetUnderlyingType(type)!;

        if (type.IsEnum)
            return false;

        // types Npgsql knows well without help
        if (type == typeof(string) ||
            type == typeof(bool) ||
            type == typeof(byte) ||
            type == typeof(short) ||
            type == typeof(int) ||
            type == typeof(long) ||
            type == typeof(float) ||
            type == typeof(double) ||
            type == typeof(decimal) ||
            type == typeof(Guid) ||
            type == typeof(DateTime) ||
            type == typeof(DateTimeOffset) ||
            type == typeof(TimeSpan) ||
            type == typeof(byte[]))
            return false;

        // arrays are handled by Npgsql as pg arrays in your code
        if (type.IsArray)
            return false;

        // dictionaries / lists / POCOs -> jsonb
        if (typeof(System.Collections.IDictionary).IsAssignableFrom(type))
            return true;
        if (typeof(System.Collections.IEnumerable).IsAssignableFrom(type) && type != typeof(string))
            return true;

        // any other POCO -> jsonb
        return true;
    }

    /// <summary>
    /// Translates '?' placeholders to PostgreSQL '@p1..@pn' and binds parameters.
    /// Handles single-quoted string literals to avoid replacing '?' inside them.
    /// </summary>
    private NpgsqlCommand PrepareCommand(NpgsqlConnection conn, string sql, List<object?>? parameters)
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
                cmd.Parameters.Add(p);
                continue;
            }

            var type = value.GetType();

            if (type.IsEnum)
            {
                p.NpgsqlDbType = NpgsqlDbType.Integer;
                p.Value = Convert.ToInt32(value);
                cmd.Parameters.Add(p);
                continue;
            }

            if (ShouldWriteAsJsonb(type))
            {
                p.NpgsqlDbType = NpgsqlDbType.Jsonb;
                p.Value = JsonSerializer.Serialize(value, _jsonOptions);
            }
            else
            {
                p.Value = value;
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

    // Backward-compatible signature (older callers / older interface)
    public Task CreateKeySpaceAsync(string keyspace)
        => CreateSchemaAsync(keyspace, CancellationToken.None);

    public Task CreateKeySpaceAsync(string keyspace, CancellationToken ct = default)
        => CreateSchemaAsync(keyspace, ct);

    public async Task CreateSchemaAsync(string schema, CancellationToken ct = default)
    {
        await EnsureConnectedAsync(ct).ConfigureAwait(false);
        await using var cmd = _conn!.CreateCommand();
        cmd.CommandText = $"CREATE SCHEMA IF NOT EXISTS \"{NormLower(schema)}\";";
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
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
                    await HealthCheckAsync().ConfigureAwait(false);
                    await Task.Delay(TimeSpan.FromSeconds(seconds), _healthCts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { /* ignore */ }
            }
        });
    }

    private async Task HealthCheckAsync()
    {
        if (!await _pingLock.WaitAsync(0).ConfigureAwait(false))
            return;

        try
        {
            // Use a separate, short-lived connection for the health check to avoid
            // "command already in progress" on the shared _conn.
            await using var pingConn = new NpgsqlConnection(BuildConnectionString());
            await pingConn.OpenAsync(_healthCts.Token).ConfigureAwait(false);

            await using var cmd = pingConn.CreateCommand();
            cmd.CommandText = "SELECT 1";
            cmd.CommandTimeout = 3;

            await cmd.ExecuteScalarAsync(_healthCts.Token).ConfigureAwait(false);

            if (!IsConnected)
            {
                IsConnected = true;
                OnConnected?.Invoke();
            }
        }
        catch (NpgsqlOperationInProgressException)
        {
            // If the pool/connection is busy for some reason, just skip this tick.
            // We don't want health checks to interfere with normal operations.
        }
        catch (OperationCanceledException)
        {
            // ignore
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
