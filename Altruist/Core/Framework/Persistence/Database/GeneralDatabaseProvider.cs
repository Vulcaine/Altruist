// Altruist.Persistence/GeneralSqlDatabaseProvider.cs
/*
Copyright 2025 Aron Gere

Licensed under the Apache License, Version 2.0 (the "License");
you may not use this file except in compliance with the License.
You may obtain a copy of the License at

    http://www.apache.org/licenses/LICENSE-2.0
*/

using System.Collections.Concurrent;
using System.Data;
using System.Data.Common;
using System.Linq.Expressions;
using System.Reflection;
using System.Text;
using System.Text.Json;

using Altruist.Contracts;

namespace Altruist.Persistence;

/// <summary>
/// Provider-agnostic ADO.NET SQL provider base. Concrete providers implement:
/// - Connection creation + connection string building
/// - Parameter binding (esp provider-specific JSON / special types)
/// </summary>
public abstract class GeneralSqlDatabaseProvider : ISqlDatabaseProvider, IGeneralDatabaseProvider
{
    private DbConnection? _conn;

    protected readonly JsonSerializerOptions JsonOptions;

    private static readonly ConcurrentDictionary<Type, UntypedMaterializer> _untypedMaterializers = new();


    private sealed record UntypedProp(string Name, Type PropType, Action<object, object?> Setter);

    private sealed class UntypedMaterializer
    {
        public required Func<object> Factory { get; init; }
        public required UntypedProp[] Props { get; init; }
    }

    protected GeneralSqlDatabaseProvider(JsonSerializerOptions jsonOptions)
    {
        JsonOptions = jsonOptions;
    }

    // ---------- Provider specifics to implement ----------

    public abstract string ServiceName { get; }
    public abstract IDatabaseServiceToken Token { get; }

    /// <summary>Default parameter prefix used for named parameters.</summary>
    protected virtual string ParameterPrefix => "@";

    /// <summary>Concrete provider builds its full connection string.</summary>
    protected abstract string BuildConnectionString(string? overrideHost = null, int? overridePort = null);

    /// <summary>Concrete provider creates its DbConnection type.</summary>
    protected abstract DbConnection CreateConnection(string connectionString);

    /// <summary>
    /// Provider-specific parameter binding. Override this to set things like NpgsqlDbType.Jsonb etc.
    /// Base implementation handles enums + JSON-by-heuristic as string.
    /// </summary>
    protected virtual void BindParameter(DbParameter p, object? value)
    {
        if (value is null)
        {
            p.Value = DBNull.Value;
            return;
        }

        var type = value.GetType();

        if (type.IsEnum)
        {
            p.Value = Convert.ToInt32(value);
            return;
        }

        if (ShouldWriteAsJson(type))
        {
            p.Value = JsonSerializer.Serialize(value, JsonOptions);
            return;
        }

        p.Value = value;
    }

    /// <summary>Ping query used for health checks.</summary>
    protected virtual string HealthCheckSql => "SELECT 1";

    public string GetConnectionString() => BuildConnectionString();

    public bool IsConnected { get; private set; }

    public event Action? OnConnected;
    public event Action<Exception>? OnFailed;
    public event Action<Exception>? OnRetryExhausted;

    // ---------- Connection lifecycle ----------

    protected async Task EnsureConnectedAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        IsConnected = _conn?.State == ConnectionState.Open;
        if (!IsConnected)
            await ConnectAsync(30, 2000, ct).ConfigureAwait(false);
    }

    // Backward compatible signature
    public Task ConnectAsync(int maxRetries, int delayMilliseconds)
        => ConnectAsync(maxRetries, delayMilliseconds, CancellationToken.None);

    /// <summary>
    /// Connect with retry. Uses the provider's default connection string.
    /// </summary>
    public async Task ConnectAsync(int maxRetries, int delayMilliseconds, CancellationToken ct = default)
    {
        await ConnectInternalAsync(
            connectionStringFactory: () => BuildConnectionString(),
            maxRetries: maxRetries,
            delayMilliseconds: delayMilliseconds,
            ct: ct).ConfigureAwait(false);
    }

    // --------------------------------------------------------------------
    // IConnectable protocol/host/port overloads (THIS fixes CS0535)
    // --------------------------------------------------------------------

    /// <summary>
    /// Backward-compatible IConnectable signature (no CancellationToken).
    /// </summary>
    public Task ConnectAsync(string protocol, string host, int port, int maxRetries, int delayMilliseconds)
        => ConnectAsync(protocol, host, port, maxRetries, delayMilliseconds, CancellationToken.None);

    /// <summary>
    /// Preferred overload with CancellationToken.
    /// </summary>
    public async Task ConnectAsync(
        string protocol,
        string host,
        int port,
        int maxRetries,
        int delayMilliseconds,
        CancellationToken ct = default)
    {
        // protocol is intentionally ignored; provider-specific SSL etc is encoded in connection string
        var hostLower = NormLower(host);
        var portValue = port <= 0 ? (int?)null : port;

        await ConnectInternalAsync(
            connectionStringFactory: () => BuildConnectionString(overrideHost: hostLower, overridePort: portValue),
            maxRetries: maxRetries,
            delayMilliseconds: delayMilliseconds,
            ct: ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Untyped query entrypoint: materializes rows into instances of <paramref name="modelType"/>.
    /// </summary>
    public async Task<List<object>> QueryAsync(
        Type modelType,
        string sql,
        List<object?>? parameters,
        CancellationToken ct)
    {
        if (modelType is null)
            throw new ArgumentNullException(nameof(modelType));

        ct.ThrowIfCancellationRequested();

        var mat = _untypedMaterializers.GetOrAdd(modelType, static t => BuildUntypedMaterializer(t));

        try
        {
            await EnsureConnectedAsync(ct).ConfigureAwait(false);

            await using var cmd = PrepareCommand(_conn!, sql, parameters);

            await using var reader = await cmd
                .ExecuteReaderAsync(CommandBehavior.SequentialAccess, ct)
                .ConfigureAwait(false);

            // Map column name -> ordinal
            var ordinals = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < reader.FieldCount; i++)
                ordinals[reader.GetName(i)] = i;

            // Bind properties that exist in the result-set, ordered by ordinal (important for SequentialAccess)
            var bindings = mat.Props
                .Select(p => (Prop: p, HasOrd: ordinals.TryGetValue(p.Name, out var ord), Ord: ord))
                .Where(x => x.HasOrd)
                .OrderBy(x => x.Ord)
                .ToArray();

            var list = new List<object>();

            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                ct.ThrowIfCancellationRequested();

                var inst = mat.Factory();

                foreach (var b in bindings)
                {
                    if (await reader.IsDBNullAsync(b.Ord, ct).ConfigureAwait(false))
                        continue;

                    var val = reader.GetValue(b.Ord);
                    if (val is null)
                        continue;

                    var targetType = Nullable.GetUnderlyingType(b.Prop.PropType) ?? b.Prop.PropType;
                    var converted = ConvertValue(val, targetType);

                    // Setter expects exact property type
                    b.Prop.Setter(inst, converted);
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

    private static UntypedMaterializer BuildUntypedMaterializer(Type modelType)
    {
        // Create instance factory (compiled)
        var ctor =
            modelType.GetConstructor(
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                binder: null,
                types: Type.EmptyTypes,
                modifiers: null);

        if (ctor is null)
            throw new InvalidOperationException(
                $"Type '{modelType.FullName}' must have a parameterless constructor to be materialized.");

        var newExpr = Expression.New(ctor);
        var factory = Expression
            .Lambda<Func<object>>(Expression.Convert(newExpr, typeof(object)))
            .Compile();

        // Build setters (compiled)
        var props = modelType
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.CanWrite)
            .Select(p =>
            {
                var target = Expression.Parameter(typeof(object), "target");
                var value = Expression.Parameter(typeof(object), "value");

                var castTarget = Expression.Convert(target, modelType);
                var member = Expression.Property(castTarget, p);
                var castValue = Expression.Convert(value, p.PropertyType);

                var assign = Expression.Assign(member, castValue);
                var setter = Expression.Lambda<Action<object, object?>>(assign, target, value).Compile();

                return new UntypedProp(p.Name, p.PropertyType, setter);
            })
            .ToArray();

        return new UntypedMaterializer { Factory = factory, Props = props };
    }

    private async Task ConnectInternalAsync(
        Func<string> connectionStringFactory,
        int maxRetries,
        int delayMilliseconds,
        CancellationToken ct)
    {
        Exception? last = null;

        for (int attempt = 1; attempt <= maxRetries; attempt++)
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                _conn = CreateConnection(connectionStringFactory());
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
    public Task ConnectAsync(CancellationToken ct) => ConnectAsync(30, 2000, ct);

    // Backward compatible signature
    public Task ShutdownAsync(Exception? ex = null) => ShutdownAsync(ex, CancellationToken.None);

    public async Task ShutdownAsync(Exception? ex = null, CancellationToken ct = default)
    {
        StopHealthChecks();

        if (_conn is null)
            return;

        try
        {
            // keep compatibility
            await _conn.CloseAsync().ConfigureAwait(false);
        }
        finally
        {
            await _conn.DisposeAsync().ConfigureAwait(false);
            _conn = null;
            IsConnected = false;
        }
    }

    // Backward compatible signature
    public Task ChangeKeyspaceAsync(string schema) => ChangeKeyspaceAsync(schema, CancellationToken.None);

    public virtual async Task ChangeKeyspaceAsync(string schema, CancellationToken ct = default)
    {
        // Default no-op unless overridden (Postgres uses SET search_path)
        await Task.CompletedTask.ConfigureAwait(false);
    }

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

    // ---------- Provider API ----------

    // Backward compatible signature
    public Task<IEnumerable<TVaultModel>> QueryAsync<TVaultModel>(string sql, List<object>? parameters = null)
        where TVaultModel : class, IVaultModel
        => QueryAsync<TVaultModel>(sql, parameters?.Cast<object?>().ToList(), CancellationToken.None);

    public async Task<IEnumerable<TVaultModel>> QueryAsync<TVaultModel>(
        string sql,
        List<object?>? parameters = null,
        CancellationToken ct = default)
        where TVaultModel : class
        => (await ExecuteFetchAsync<TVaultModel>(sql, parameters, ct).ConfigureAwait(false)).ToList();

    // Backward compatible signature
    public Task<TVaultModel?> QuerySingleAsync<TVaultModel>(string sql, List<object>? parameters = null)
        where TVaultModel : class
        => QuerySingleAsync<TVaultModel>(sql, parameters?.Cast<object?>().ToList(), CancellationToken.None);

    public async Task<TVaultModel?> QuerySingleAsync<TVaultModel>(
        string sql,
        List<object?>? parameters = null,
        CancellationToken ct = default)
        where TVaultModel : class
        => (await ExecuteFetchAsync<TVaultModel>(sql, parameters, ct).ConfigureAwait(false)).FirstOrDefault();

    // Backward compatible signature
    public Task<long> ExecuteCountAsync(string sql, List<object>? parameters = null)
        => ExecuteCountAsync(sql, parameters?.Cast<object?>().ToList(), CancellationToken.None);

    public async Task<long> ExecuteCountAsync(string sql, List<object?>? parameters = null, CancellationToken ct = default)
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

    // Backward compatible signature
    public Task<long> ExecuteAsync(string sql, List<object>? parameters = null)
        => ExecuteAsync(sql, parameters?.Cast<object?>().ToList(), CancellationToken.None);

    public async Task<long> ExecuteAsync(string sql, List<object?>? parameters = null, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        await EnsureConnectedAsync(ct).ConfigureAwait(false);
        await using var cmd = PrepareCommand(_conn!, sql, parameters);

        var affected = await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        if (!IsConnected)
            RaiseConnectedEvent();

        return affected;
    }

    // Default stubs (you can keep parity with your other provider surface)
    public Task<long> UpdateAsync<TVaultModel>(TVaultModel entity) where TVaultModel : class, IVaultModel
        => UpdateAsync(entity, CancellationToken.None);

    public virtual async Task<long> UpdateAsync<TVaultModel>(TVaultModel entity, CancellationToken ct = default)
        where TVaultModel : class, IVaultModel
    {
        await Task.CompletedTask.ConfigureAwait(false);
        return 1;
    }

    public Task<long> DeleteAsync<TVaultModel>(TVaultModel entity) where TVaultModel : class, IVaultModel
        => DeleteAsync(entity, CancellationToken.None);

    public virtual async Task<long> DeleteAsync<TVaultModel>(TVaultModel entity, CancellationToken ct = default)
        where TVaultModel : class, IVaultModel
    {
        await Task.CompletedTask.ConfigureAwait(false);
        return 1;
    }

    // Schema
    public Task CreateKeySpaceAsync(string keyspace) => CreateSchemaAsync(keyspace, CancellationToken.None);
    public Task CreateKeySpaceAsync(string keyspace, CancellationToken ct = default) => CreateSchemaAsync(keyspace, ct);

    public virtual async Task CreateSchemaAsync(string schema, CancellationToken ct = default)
    {
        await EnsureConnectedAsync(ct).ConfigureAwait(false);

        await using var cmd = _conn!.CreateCommand();
        cmd.CommandText = $"CREATE SCHEMA IF NOT EXISTS \"{NormLower(schema)}\";";
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    // ---------- Internals: materialization + parameterization ----------

    private async Task<IEnumerable<TVaultModel>> ExecuteFetchAsync<TVaultModel>(
        string sql,
        List<object?>? parameters,
        CancellationToken ct)
        where TVaultModel : class
    {
        ct.ThrowIfCancellationRequested();

        try
        {
            await EnsureConnectedAsync(ct).ConfigureAwait(false);

            await using var cmd = PrepareCommand(_conn!, sql, parameters);

            await using var reader = await cmd
                .ExecuteReaderAsync(CommandBehavior.SequentialAccess, ct)
                .ConfigureAwait(false);

            var list = new List<TVaultModel>();
            var type = typeof(TVaultModel);

            var props = type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(p => p.CanWrite)
                .ToArray();

            var ordinals = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < reader.FieldCount; i++)
                ordinals[reader.GetName(i)] = i;

            // SequentialAccess => read in ordinal order
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
                    if (await reader.IsDBNullAsync(b.Ord, ct).ConfigureAwait(false))
                        continue;

                    var val = reader.GetValue(b.Ord);
                    if (val is null)
                        continue;

                    var targetType = Nullable.GetUnderlyingType(b.Prop.PropertyType) ?? b.Prop.PropertyType;
                    b.Prop.SetValue(inst, ConvertValue(val, targetType));
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

    protected virtual object? ConvertValue(object val, Type targetType)
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
            string json => JsonSerializer.Deserialize(json, targetType, JsonOptions),
            JsonDocument doc => doc.Deserialize(targetType, JsonOptions),
            _ => null
        };

        return result is not null;
    }

    protected static bool IsJsonTargetType(Type t)
    {
        if (typeof(System.Collections.IDictionary).IsAssignableFrom(t))
            return true;

        if (typeof(System.Collections.IEnumerable).IsAssignableFrom(t))
            return t != typeof(string);

        return !t.IsPrimitive
               && t != typeof(decimal)
               && t != typeof(Guid)
               && t != typeof(DateTime)
               && t != typeof(DateTimeOffset)
               && t != typeof(TimeSpan);
    }

    protected static bool ShouldWriteAsJson(Type type)
    {
        if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Nullable<>))
            type = Nullable.GetUnderlyingType(type)!;

        if (type.IsEnum)
            return false;

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

        if (type.IsArray)
            return false;

        if (typeof(System.Collections.IDictionary).IsAssignableFrom(type))
            return true;

        if (typeof(System.Collections.IEnumerable).IsAssignableFrom(type) && type != typeof(string))
            return true;

        return true;
    }

    protected DbCommand PrepareCommand(DbConnection conn, string sql, List<object?>? parameters)
    {
        var cmd = conn.CreateCommand();

        if (parameters is null || parameters.Count == 0)
        {
            cmd.CommandText = sql;
            return cmd;
        }

        cmd.CommandText = ReplaceQuestionMarks(sql, parameters.Count, ParameterPrefix);

        for (int i = 0; i < parameters.Count; i++)
        {
            var p = cmd.CreateParameter();
            p.ParameterName = $"{ParameterPrefix}p{i + 1}";

            BindParameter(p, parameters[i]);
            cmd.Parameters.Add(p);
        }

        return cmd;
    }

    protected static string ReplaceQuestionMarks(string sql, int expectedCount, string prefix)
    {
        var sb = new StringBuilder(sql.Length + expectedCount * 3);
        bool inSingle = false;
        bool inDouble = false;
        int paramIndex = 0;

        for (int i = 0; i < sql.Length; i++)
        {
            char c = sql[i];

            if (!inDouble && c == '\'')
            {
                sb.Append(c);
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
                sb.Append(prefix).Append('p').Append(paramIndex);
                continue;
            }

            sb.Append(c);
        }

        return sb.ToString();
    }

    protected static string NormLower(string? s) => (s ?? string.Empty).Trim().ToLowerInvariant();

    // ---------- Health checks ----------

    private CancellationTokenSource _healthCts = new();
    private readonly SemaphoreSlim _pingLock = new(1, 1);

    protected void StartHealthChecks(int seconds = 5)
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
                catch (OperationCanceledException) { }
            }
        });
    }

    protected virtual async Task HealthCheckAsync()
    {
        if (!await _pingLock.WaitAsync(0).ConfigureAwait(false))
            return;

        try
        {
            await using var pingConn = CreateConnection(BuildConnectionString());
            await pingConn.OpenAsync(_healthCts.Token).ConfigureAwait(false);

            await using var cmd = pingConn.CreateCommand();
            cmd.CommandText = HealthCheckSql;
            cmd.CommandTimeout = 3;

            await cmd.ExecuteScalarAsync(_healthCts.Token).ConfigureAwait(false);

            if (!IsConnected)
            {
                IsConnected = true;
                OnConnected?.Invoke();
            }
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

    protected void StopHealthChecks() => _healthCts.Cancel();
}
