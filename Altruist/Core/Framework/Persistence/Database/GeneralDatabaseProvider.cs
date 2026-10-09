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
/// <remarks>
/// <para>
/// Derive from this to add a new ADO.NET database; application code should inject <see cref="ISqlDatabaseProvider"/>
/// (or better, <see cref="IVault{TVaultModel}"/>) rather than this class. Postgres ships as <c>PgSqlDbProvider</c>.
/// </para>
/// <para>
/// Every query/command leases its own pooled connection (or the ambient transaction's, see
/// <see cref="InTransactionAsync{T}"/>), so instances are safe to share as singletons. A separate control connection
/// opened by <c>ConnectAsync</c> drives connection state, plus a background health check every 5 seconds.
/// </para>
/// </remarks>
public abstract class GeneralSqlDatabaseProvider : ISqlDatabaseProvider, IGeneralDatabaseProvider, ISqlTransactionProvider
{
    private DbConnection? _conn;

    /// <summary>Serializer options used to write JSON parameters and read JSON columns.</summary>
    protected readonly JsonSerializerOptions JsonOptions;

    private static readonly ConcurrentDictionary<Type, UntypedMaterializer> _untypedMaterializers = new();

    private static VaultDocument? TryGetDocument(Type t)
    {
        return VaultDocument.From(t);
    }

    private sealed record UntypedProp(
        string LogicalName,
        Type PropType,
        Action<object, object?> Setter,
        string[] CandidateColumns);

    private sealed class UntypedMaterializer
    {
        public required Func<object> Factory { get; init; }
        public required UntypedProp[] Props { get; init; }
    }

    /// <summary>Initializes the provider.</summary>
    /// <param name="jsonOptions">Options for (de)serializing JSON-mapped values.</param>
    protected GeneralSqlDatabaseProvider(JsonSerializerOptions jsonOptions)
    {
        JsonOptions = jsonOptions;
    }

    // ---------- Provider specifics to implement ----------

    /// <summary>Human-readable service name (used in logs and health reporting).</summary>
    public abstract string ServiceName { get; }
    /// <inheritdoc/>
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

    /// <inheritdoc/>
    public string GetConnectionString() => BuildConnectionString();

    /// <summary>
    /// <c>true</c> while the database is reachable, as last observed by connect, a query or the 5-second health check.
    /// </summary>
    public bool IsConnected { get; private set; }

    /// <summary>Raised when the connection is (re)established. Also raised after every typed/untyped query while connected, so handlers must be cheap and idempotent.</summary>
    public event Action? OnConnected;
    /// <summary>Raised on the first failed connect attempt and when a health check finds a previously healthy database unreachable.</summary>
    public event Action<Exception>? OnFailed;
    /// <summary>Raised when <see cref="ConnectAsync(int, int, CancellationToken)"/> has used up all its retries without connecting.</summary>
    public event Action<Exception>? OnRetryExhausted;

    /// <summary>
    /// Creates and opens a fresh pooled connection for thread-safe per-operation use.
    /// </summary>
    protected async Task<DbConnection> GetPooledConnectionAsync(CancellationToken ct = default)
    {
        var conn = CreateConnection(BuildConnectionString());
        await conn.OpenAsync(ct).ConfigureAwait(false);
        return conn;
    }

    /// <summary>
    /// A connection for one operation: the ambient transaction's connection when one is bound to
    /// this async flow (see <see cref="SqlAmbientTransaction"/>), otherwise a fresh pooled one.
    /// </summary>
    protected readonly struct ConnectionLease : IAsyncDisposable
    {
        private readonly SemaphoreSlim? _gate;

        /// <summary>Wraps a leased connection.</summary>
        /// <param name="connection">The open connection.</param>
        /// <param name="transaction">The ambient transaction, or <c>null</c> for a private pooled connection.</param>
        /// <param name="gate">The ambient transaction's gate to release on dispose, or <c>null</c>.</param>
        public ConnectionLease(DbConnection connection, DbTransaction? transaction, SemaphoreSlim? gate)
        {
            Connection = connection;
            Transaction = transaction;
            _gate = gate;
        }

        /// <summary>The open connection to run the command on.</summary>
        public DbConnection Connection { get; }
        /// <summary>The ambient transaction to attach to the command, or <c>null</c>.</summary>
        public DbTransaction? Transaction { get; }

        /// <summary>Disposes a private pooled connection, or releases the ambient transaction's gate (the connection stays open for the transaction).</summary>
        public ValueTask DisposeAsync()
        {
            if (Transaction is null)
                return Connection.DisposeAsync();
            _gate?.Release();
            return ValueTask.CompletedTask;
        }
    }

    /// <summary>
    /// Leases a connection for one operation: the ambient transaction's connection (serialized through its gate) when
    /// one is active on this async flow, otherwise a fresh pooled connection. Always dispose the lease.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    protected async Task<ConnectionLease> LeaseConnectionAsync(CancellationToken ct = default)
    {
        var ambient = SqlAmbientTransaction.Current;
        if (ambient is not null)
        {
            await ambient.Gate.WaitAsync(ct).ConfigureAwait(false);
            return new ConnectionLease(ambient.Connection, ambient.Transaction, ambient.Gate);
        }

        return new ConnectionLease(await GetPooledConnectionAsync(ct).ConfigureAwait(false), null, null);
    }

    /// <inheritdoc />
    public async Task<T> InTransactionAsync<T>(
        Func<CancellationToken, Task<T>> work,
        IsolationLevel isolation = IsolationLevel.ReadCommitted,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(work);
        if (SqlAmbientTransaction.Current is not null)
            return await work(ct).ConfigureAwait(false);

        var conn = await GetPooledConnectionAsync(ct).ConfigureAwait(false);
        return await SqlAmbientTransactionRunner.RunAsync(conn, work, isolation, ct).ConfigureAwait(false);
    }

    // ---------- Connection lifecycle ----------

    /// <summary>Reconnects the control connection (30 attempts, 2000 ms apart) if it is not open.</summary>
    /// <param name="ct">Cancellation token.</param>
    protected async Task EnsureConnectedAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        IsConnected = _conn?.State == ConnectionState.Open;
        if (!IsConnected)
            await ConnectAsync(30, 2000, ct).ConfigureAwait(false);
    }

    // Backward compatible signature
    /// <summary>Same as <see cref="ConnectAsync(int, int, CancellationToken)"/> without cancellation.</summary>
    /// <param name="maxRetries">Maximum connection attempts.</param>
    /// <param name="delayMilliseconds">Delay between attempts, in milliseconds.</param>
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

    /// <inheritdoc/>
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
            await using var lease = await LeaseConnectionAsync(ct).ConfigureAwait(false);
        var conn = lease.Connection;

            await using var cmd = PrepareCommand(conn, sql, parameters);
        cmd.Transaction = lease.Transaction;

            await using var reader = await cmd
                .ExecuteReaderAsync(CommandBehavior.SequentialAccess, ct)
                .ConfigureAwait(false);

            // column name -> ordinal (physical names, unless SQL aliases them)
            var ordinals = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < reader.FieldCount; i++)
                ordinals[reader.GetName(i)] = i;

            static bool TryFindOrdinal(
                Dictionary<string, int> ords,
                string[] names,
                out int ord)
            {
                foreach (var n in names)
                {
                    if (!string.IsNullOrWhiteSpace(n) && ords.TryGetValue(n, out ord))
                        return true;
                }

                ord = -1;
                return false;
            }

            var bindings = mat.Props
                .Select(p => (Prop: p, HasOrd: TryFindOrdinal(ordinals, p.CandidateColumns, out var o), Ord: o))
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
        var doc = TryGetDocument(modelType);

        // factory
        var ctor = modelType.GetConstructor(
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            binder: null,
            types: Type.EmptyTypes,
            modifiers: null);

        if (ctor is null)
            throw new InvalidOperationException(
                $"Type '{modelType.FullName}' must have a parameterless constructor to be materialized.");

        var factory = Expression
            .Lambda<Func<object>>(Expression.Convert(Expression.New(ctor), typeof(object)))
            .Compile();

        // setters
        var props = modelType
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(p =>
            {
                var setMethod = p.GetSetMethod(nonPublic: true);
                if (setMethod is null)
                    return null; // skip read-only

                // candidate DB column names for this CLR property
                var names = new List<string>(capacity: 3) { p.Name };

                // Document mapping: logical CLR name -> physical DB column name
                if (doc is not null && doc.Columns.TryGetValue(p.Name, out var physical))
                    names.Add(physical);

                // extra cheap fallback for common PK naming mismatches
                // (helps if base model uses StorageId/Id but DB column is "id")
                if (p.Name.Equals("StorageId", StringComparison.OrdinalIgnoreCase) ||
                    p.Name.Equals("Id", StringComparison.OrdinalIgnoreCase))
                {
                    if (!names.Contains("id", StringComparer.OrdinalIgnoreCase))
                        names.Add("id");
                }

                Action<object, object?> setter;

                // Prefer compiled call to setter; if it fails (visibility), fall back to cached reflection setter.
                try
                {
                    var target = Expression.Parameter(typeof(object), "target");
                    var value = Expression.Parameter(typeof(object), "value");

                    var castTarget = Expression.Convert(target, modelType);
                    var castValue = Expression.Convert(value, p.PropertyType);

                    var call = Expression.Call(castTarget, setMethod, castValue);
                    setter = Expression.Lambda<Action<object, object?>>(call, target, value).Compile();
                }
                catch
                {
                    // still cached (PropertyInfo captured once), but uses reflection per invocation
                    setter = (obj, val) => p.SetValue(obj, val);
                }

                return new UntypedProp(
                    LogicalName: p.Name,
                    PropType: p.PropertyType,
                    Setter: setter,
                    CandidateColumns: names.ToArray());
            })
            .Where(x => x is not null)
            .Select(x => x!)
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

    /// <summary>Connects with the default policy: 30 attempts, 2000 ms apart.</summary>
    public Task ConnectAsync() => ConnectAsync(30, 2000, CancellationToken.None);
    /// <summary>Connects with the default policy (30 attempts, 2000 ms apart), honouring <paramref name="ct"/>.</summary>
    /// <param name="ct">Cancellation token.</param>
    public Task ConnectAsync(CancellationToken ct) => ConnectAsync(30, 2000, ct);

    // Backward compatible signature
    /// <summary>Same as <see cref="ShutdownAsync(Exception, CancellationToken)"/> without cancellation.</summary>
    /// <param name="ex">Optional reason for the shutdown (informational).</param>
    public Task ShutdownAsync(Exception? ex = null) => ShutdownAsync(ex, CancellationToken.None);

    /// <inheritdoc/>
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
    /// <summary>Same as <see cref="ChangeKeyspaceAsync(string, CancellationToken)"/> without cancellation.</summary>
    /// <param name="schema">Schema name.</param>
    public Task ChangeKeyspaceAsync(string schema) => ChangeKeyspaceAsync(schema, CancellationToken.None);

    /// <summary>Switches the default schema. No-op in the base class; providers override it (Postgres runs <c>SET search_path</c>).</summary>
    /// <param name="schema">Schema name.</param>
    /// <param name="ct">Cancellation token.</param>
    public virtual async Task ChangeKeyspaceAsync(string schema, CancellationToken ct = default)
    {
        // Default no-op unless overridden (Postgres uses SET search_path)
        await Task.CompletedTask.ConfigureAwait(false);
    }

    /// <summary>Marks the provider connected and raises <see cref="OnConnected"/>.</summary>
    public void RaiseConnectedEvent()
    {
        IsConnected = true;
        OnConnected?.Invoke();
    }

    /// <summary>Marks the provider disconnected and raises <see cref="OnFailed"/>.</summary>
    /// <param name="ex">The failure.</param>
    public void RaiseFailedEvent(Exception ex)
    {
        IsConnected = false;
        OnFailed?.Invoke(ex);
    }

    /// <summary>Marks the provider disconnected and raises <see cref="OnRetryExhausted"/>.</summary>
    /// <param name="ex">The last connection failure.</param>
    public void RaiseOnRetryExhaustedEvent(Exception ex)
    {
        IsConnected = false;
        OnRetryExhausted?.Invoke(ex);
    }

    // ---------- Provider API ----------

    // Backward compatible signature
    /// <summary>Vault-model-only overload of <see cref="QueryAsync{TVaultModel}(string, List{object}, CancellationToken)"/> without cancellation.</summary>
    /// <param name="sql">SQL text with <c>?</c> placeholders.</param>
    /// <param name="parameters">Positional values for the placeholders, or <c>null</c>.</param>
    public Task<IEnumerable<TVaultModel>> QueryAsync<TVaultModel>(string sql, List<object>? parameters = null)
        where TVaultModel : class, IVaultModel
        => QueryAsync<TVaultModel>(sql, parameters?.Cast<object?>().ToList(), CancellationToken.None);

    /// <inheritdoc/>
    public async Task<IEnumerable<TVaultModel>> QueryAsync<TVaultModel>(
        string sql,
        List<object?>? parameters = null,
        CancellationToken ct = default)
        => (await ExecuteFetchAsync<TVaultModel>(sql, parameters, ct).ConfigureAwait(false)).ToList();

    // Backward compatible signature
    /// <summary>Overload of <see cref="QuerySingleAsync{TVaultModel}(string, List{object}, CancellationToken)"/> without cancellation.</summary>
    /// <param name="sql">SQL text with <c>?</c> placeholders.</param>
    /// <param name="parameters">Positional values for the placeholders, or <c>null</c>.</param>
    public Task<TVaultModel?> QuerySingleAsync<TVaultModel>(string sql, List<object>? parameters = null)
        where TVaultModel : class
        => QuerySingleAsync<TVaultModel>(sql, parameters?.Cast<object?>().ToList(), CancellationToken.None);

    /// <inheritdoc/>
    public async Task<TVaultModel?> QuerySingleAsync<TVaultModel>(
        string sql,
        List<object?>? parameters = null,
        CancellationToken ct = default)
        where TVaultModel : class
        => (await ExecuteFetchAsync<TVaultModel>(sql, parameters, ct).ConfigureAwait(false)).FirstOrDefault();

    // Backward compatible signature
    /// <summary>Overload of <see cref="ExecuteCountAsync(string, List{object}, CancellationToken)"/> without cancellation.</summary>
    /// <param name="sql">SQL text with <c>?</c> placeholders.</param>
    /// <param name="parameters">Positional values for the placeholders, or <c>null</c>.</param>
    public Task<long> ExecuteCountAsync(string sql, List<object>? parameters = null)
        => ExecuteCountAsync(sql, parameters?.Cast<object?>().ToList(), CancellationToken.None);

    /// <inheritdoc/>
    public async Task<long> ExecuteCountAsync(string sql, List<object?>? parameters = null, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        await using var lease = await LeaseConnectionAsync(ct).ConfigureAwait(false);
        var conn = lease.Connection;
        await using var cmd = PrepareCommand(conn, sql, parameters);
        cmd.Transaction = lease.Transaction;

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
    /// <summary>Overload of <see cref="ExecuteAsync(string, List{object}, CancellationToken)"/> without cancellation.</summary>
    /// <param name="sql">SQL text with <c>?</c> placeholders.</param>
    /// <param name="parameters">Positional values for the placeholders, or <c>null</c>.</param>
    public Task<long> ExecuteAsync(string sql, List<object>? parameters = null)
        => ExecuteAsync(sql, parameters?.Cast<object?>().ToList(), CancellationToken.None);

    /// <inheritdoc/>
    public async Task<long> ExecuteAsync(string sql, List<object?>? parameters = null, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        await using var lease = await LeaseConnectionAsync(ct).ConfigureAwait(false);
        var conn = lease.Connection;
        await using var cmd = PrepareCommand(conn, sql, parameters);
        cmd.Transaction = lease.Transaction;

        var affected = await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        if (!IsConnected)
            RaiseConnectedEvent();

        return affected;
    }

    // Default stubs (you can keep parity with your other provider surface)
    /// <summary>Reserved no-op (returns 1); see <see cref="UpdateAsync{TVaultModel}(TVaultModel, CancellationToken)"/>.</summary>
    /// <param name="entity">Ignored.</param>
    public Task<long> UpdateAsync<TVaultModel>(TVaultModel entity) where TVaultModel : class, IVaultModel
        => UpdateAsync(entity, CancellationToken.None);

    /// <inheritdoc/>
    public virtual async Task<long> UpdateAsync<TVaultModel>(TVaultModel entity, CancellationToken ct = default)
        where TVaultModel : class, IVaultModel
    {
        await Task.CompletedTask.ConfigureAwait(false);
        return 1;
    }

    /// <summary>Reserved no-op (returns 1); see <see cref="DeleteAsync{TVaultModel}(TVaultModel, CancellationToken)"/>.</summary>
    /// <param name="entity">Ignored.</param>
    public Task<long> DeleteAsync<TVaultModel>(TVaultModel entity) where TVaultModel : class, IVaultModel
        => DeleteAsync(entity, CancellationToken.None);

    /// <inheritdoc/>
    public virtual async Task<long> DeleteAsync<TVaultModel>(TVaultModel entity, CancellationToken ct = default)
        where TVaultModel : class, IVaultModel
    {
        await Task.CompletedTask.ConfigureAwait(false);
        return 1;
    }

    // Schema
    /// <summary>Same as <see cref="CreateSchemaAsync"/> without cancellation.</summary>
    /// <param name="keyspace">Schema name.</param>
    public Task CreateKeySpaceAsync(string keyspace) => CreateSchemaAsync(keyspace, CancellationToken.None);
    /// <summary>Alias of <see cref="CreateSchemaAsync"/>.</summary>
    /// <param name="keyspace">Schema name.</param>
    /// <param name="ct">Cancellation token.</param>
    public Task CreateKeySpaceAsync(string keyspace, CancellationToken ct = default) => CreateSchemaAsync(keyspace, ct);

    /// <inheritdoc/>
    public virtual async Task CreateSchemaAsync(string schema, CancellationToken ct = default)
    {
        await using var lease = await LeaseConnectionAsync(ct).ConfigureAwait(false);
        var conn = lease.Connection;

        await using var cmd = conn.CreateCommand();
        cmd.Transaction = lease.Transaction;
        // Schema name is framework-controlled (from config), not user input
#pragma warning disable CA2100
        cmd.CommandText = $"CREATE SCHEMA IF NOT EXISTS \"{NormLower(schema)}\";";
#pragma warning restore CA2100
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    // ---------- Internals: materialization + parameterization ----------

    private async Task<IEnumerable<TVaultModel>> ExecuteFetchAsync<TVaultModel>(
        string sql,
        List<object?>? parameters,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        try
        {
            await using var lease = await LeaseConnectionAsync(ct).ConfigureAwait(false);
        var conn = lease.Connection;

            await using var cmd = PrepareCommand(conn, sql, parameters);
        cmd.Transaction = lease.Transaction;

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

    /// <summary>
    /// Converts a raw column value to a property type when materializing rows: strings via <c>ToString</c>, enums from
    /// name or number, JSON text/documents into complex types, GUIDs and dates, otherwise <see cref="Convert.ChangeType(object, Type)"/>.
    /// Override to support provider-specific types.
    /// </summary>
    /// <param name="val">The non-null value read from the reader.</param>
    /// <param name="targetType">The property type (nullable already unwrapped).</param>
    /// <returns>The converted value.</returns>
    protected virtual object? ConvertValue(object val, Type targetType)
    {
        if (val is null)
            return null;

        if (targetType == typeof(string))
            return val.ToString();

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

    /// <summary>Whether a property of type <paramref name="t"/> is read from a JSON column (collections, dictionaries and non-primitive objects).</summary>
    /// <param name="t">The property type.</param>
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

    /// <summary>Whether a parameter value of type <paramref name="type"/> is bound as serialized JSON (anything that isn't a scalar, enum, <c>byte[]</c> or array).</summary>
    /// <param name="type">The value's runtime type.</param>
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

    // SQL is built by the Vault ORM query pipeline — not from user input.
    // Parameters are always bound via DbParameter (parameterized queries).
#pragma warning disable CA2100
    /// <summary>Creates a command on <paramref name="conn"/>, rewriting <c>?</c> placeholders to named parameters and binding <paramref name="parameters"/> through <see cref="BindParameter"/>.</summary>
    /// <param name="conn">Open connection.</param>
    /// <param name="sql">SQL text with <c>?</c> placeholders.</param>
    /// <param name="parameters">Positional values, or <c>null</c>.</param>
    /// <returns>The prepared command (caller disposes it and sets its transaction).</returns>
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
#pragma warning restore CA2100

    /// <summary>Replaces each <c>?</c> outside single- or double-quoted text with <c>{prefix}p1</c>, <c>{prefix}p2</c>, ... in order.</summary>
    /// <param name="sql">SQL text.</param>
    /// <param name="expectedCount">Expected number of placeholders (capacity hint only; not validated).</param>
    /// <param name="prefix">Parameter prefix, e.g. <c>@</c>.</param>
    /// <returns>The rewritten SQL.</returns>
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

    /// <summary>Trims and lower-cases an identifier (<c>null</c> becomes empty).</summary>
    /// <param name="s">The identifier.</param>
    protected static string NormLower(string? s) => (s ?? string.Empty).Trim().ToLowerInvariant();

    // ---------- Health checks ----------

    private CancellationTokenSource _healthCts = new();
    private readonly SemaphoreSlim _pingLock = new(1, 1);

    /// <summary>Starts a background loop that runs <see cref="HealthCheckAsync"/> every <paramref name="seconds"/> seconds until <see cref="StopHealthChecks"/>.</summary>
    /// <param name="seconds">Interval between checks, in seconds.</param>
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

    /// <summary>
    /// Opens a separate connection and runs <see cref="HealthCheckSql"/> (3 s timeout); flips <see cref="IsConnected"/> and raises
    /// <see cref="OnConnected"/>/<see cref="OnFailed"/> on state changes. Overlapping checks are skipped.
    /// </summary>
    protected virtual async Task HealthCheckAsync()
    {
        if (!await _pingLock.WaitAsync(0).ConfigureAwait(false))
            return;

        try
        {
            await using var pingConn = CreateConnection(BuildConnectionString());
            await pingConn.OpenAsync(_healthCts.Token).ConfigureAwait(false);

            await using var cmd = pingConn.CreateCommand();
            // HealthCheckSql is a constant ("SELECT 1"), not user input
#pragma warning disable CA2100
            cmd.CommandText = HealthCheckSql;
#pragma warning restore CA2100
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

    /// <summary>Cancels the health-check loop.</summary>
    protected void StopHealthChecks()
    {
        _healthCts.Cancel();
        _healthCts.Dispose();
    }
}
