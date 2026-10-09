/*
Copyright 2025 Aron Gere

Licensed under the Apache License, Version 2.0 (the "License");
you may not use this file except in compliance with the License.
You may obtain a copy of the License at

    http://www.apache.org/licenses/LICENSE-2.0

Unless required by applicable law or agreed to in writing, software
distributed under the License is distributed on an "AS IS" BASIS,
WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
See the License for the specific language governing permissions and
limitations under the License.
*/

using Altruist.Contracts;

using Microsoft.Extensions.DependencyInjection;

namespace Altruist.Persistence;

/// <summary>
/// Base class for provider-specific keyspace bootstrappers: holds the service collection, the keyspace instance
/// and the vault model types to register for it, and builds them in <see cref="Build"/>.
/// </summary>
/// <remarks>Framework extension point for database providers; application code doesn't use it.</remarks>
/// <typeparam name="TKeyspace">The keyspace type being set up.</typeparam>
public abstract class KeyspaceSetup<TKeyspace> : IKeyspaceSetup where TKeyspace : class, IKeyspace
{
    /// <summary>The service collection vaults are registered into.</summary>
    protected readonly IServiceCollection Services;
    /// <summary>Vault model types collected for this keyspace.</summary>
    protected readonly List<Type> VaultModels = new();
    /// <summary>The keyspace being set up.</summary>
    protected readonly TKeyspace Instance;

    /// <inheritdoc/>
    public IDatabaseServiceToken Token { get; }

    /// <summary>Creates the setup for <paramref name="instance"/>.</summary>
    /// <param name="services">Service collection to register into.</param>
    /// <param name="instance">The keyspace instance.</param>
    /// <param name="token">Token of the database provider owning the keyspace.</param>
    public KeyspaceSetup(IServiceCollection services, TKeyspace instance, IDatabaseServiceToken token)
    {
        Services = services;
        Instance = instance;
        Token = token;
    }

    /// <inheritdoc/>
    public abstract Task Build();
}

/// <summary>Provider-side bootstrapper for one keyspace (schema) and its vaults.</summary>
public interface IKeyspaceSetup
{
    /// <summary>Token of the database provider owning the keyspace.</summary>
    IDatabaseServiceToken Token { get; }
    /// <summary>Creates the keyspace and registers/builds its vaults.</summary>
    Task Build();
}


/// <summary>
/// Low-level SQL provider: runs raw SQL with positional <c>?</c> parameters and materializes rows by property name.
/// Used by the SQL vaults, migrations and token stores; implemented by <see cref="GeneralSqlDatabaseProvider"/>
/// (Postgres: <c>PgSqlDbProvider</c>, registered as a singleton when <c>altruist:persistence:database:provider</c> is
/// <c>postgres</c>).
/// </summary>
/// <remarks>
/// <para>
/// When to use: only for SQL the vault API can't express (aggregates, bulk statements, custom DDL). For normal CRUD
/// inject <see cref="IVault{TVaultModel}"/>; for joins use <c>Altruist.Querying.IVaultQuery</c>.
/// </para>
/// <para>
/// Each call leases its own pooled connection, or the ambient transaction's connection when called inside
/// <c>ISqlTransactionProvider.InTransactionAsync</c>, so it is safe to use concurrently. Each <c>?</c> outside quoted
/// text is replaced by a named parameter in order; enums bind as <c>int</c>, non-scalar objects and collections bind as
/// JSON. Results map columns to properties by name (case-insensitive), so alias columns to property names
/// (<c>SELECT "display-name" AS "DisplayName"</c>).
/// </para>
/// </remarks>
/// <example>
/// <code>
/// var rows = await db.QueryAsync&lt;LevelCount&gt;(
///     "SELECT \"level\" AS \"Level\", COUNT(*) AS \"Count\" FROM \"altruist\".\"player_profile\" WHERE \"level\" &gt;= ? GROUP BY \"level\"",
///     new List&lt;object?&gt; { 10 });
/// </code>
/// </example>
public interface ISqlDatabaseProvider : IGeneralDatabaseProvider
{
    /// <summary>
    /// Opens the provider's control connection (replacing any previous one), retrying up to <paramref name="maxRetries"/>
    /// times <paramref name="delayMilliseconds"/> ms apart, then starts a background health check (every 5 s; one loop
    /// per provider). When the retries run out it raises <c>OnRetryExhausted</c> and then throws the last connection error.
    /// </summary>
    /// <param name="maxRetries">Maximum connection attempts.</param>
    /// <param name="delayMilliseconds">Delay between attempts, in milliseconds.</param>
    /// <param name="ct">Cancellation token.</param>
    Task ConnectAsync(int maxRetries, int delayMilliseconds, CancellationToken ct = default);
    /// <summary>Stops health checks and closes the control connection. Idempotent; the provider can connect again afterwards.</summary>
    /// <param name="ex">Optional reason for the shutdown (informational).</param>
    /// <param name="ct">Cancellation token.</param>
    Task ShutdownAsync(Exception? ex = null, CancellationToken ct = default);

    // Query APIs (parameter list aligns with Vaults using "?" placeholders)
    /// <summary>Runs a query and maps each row onto a new <typeparamref name="TVaultModel"/> by property name (case-insensitive; unmatched columns are ignored).</summary>
    /// <typeparam name="TVaultModel">Result type; needs a public parameterless constructor and settable properties (any class, not only vault models).</typeparam>
    /// <param name="sql">SQL text with <c>?</c> placeholders.</param>
    /// <param name="parameters">Positional values for the placeholders, or <c>null</c>.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<IEnumerable<TVaultModel>> QueryAsync<TVaultModel>(
        string sql,
        List<object?>? parameters = null,
        CancellationToken ct = default);

    /// <summary>
    /// Untyped variant of <see cref="QueryAsync{TVaultModel}(string, List{object}, CancellationToken)"/> for a runtime
    /// <paramref name="modelType"/>. Also matches the physical column names of a <c>[Vault]</c> model's
    /// <c>[VaultColumn]</c> mapping (and <c>id</c> for <c>StorageId</c>), so aliases are not needed.
    /// </summary>
    /// <param name="modelType">Type to materialize; needs a parameterless constructor (may be non-public).</param>
    /// <param name="sql">SQL text with <c>?</c> placeholders.</param>
    /// <param name="parameters">Positional values for the placeholders, or <c>null</c>.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<List<object>> QueryAsync(
        Type modelType,
        string sql,
        List<object?>? parameters,
        CancellationToken ct);

    /// <summary>Runs a query and returns the first mapped row, or <c>null</c> (does not add a <c>LIMIT</c>; include one yourself).</summary>
    /// <param name="sql">SQL text with <c>?</c> placeholders.</param>
    /// <param name="parameters">Positional values for the placeholders, or <c>null</c>.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<TVaultModel?> QuerySingleAsync<TVaultModel>(
        string sql,
        List<object?>? parameters = null,
        CancellationToken ct = default)
        where TVaultModel : class;

    /// <summary>Runs a scalar query (typically <c>SELECT COUNT(*) ...</c>) and returns the first column of the first row as <see cref="long"/> (0 when <c>null</c>).</summary>
    /// <param name="sql">SQL text with <c>?</c> placeholders.</param>
    /// <param name="parameters">Positional values for the placeholders, or <c>null</c>.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<long> ExecuteCountAsync(
        string sql,
        List<object?>? parameters = null,
        CancellationToken ct = default);

    /// <summary>Executes INSERT/UPDATE/DELETE or batched statements; returns affected rows (driver-dependent).</summary>
    /// <param name="sql">SQL text with <c>?</c> placeholders (several statements may be separated by <c>;</c>).</param>
    /// <param name="parameters">Positional values for the placeholders, or <c>null</c>.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<long> ExecuteAsync(
        string sql,
        List<object?>? parameters = null,
        CancellationToken ct = default);

    // Bootstrap / DDL
    /// <summary>Creates the SQL schema if it doesn't exist (<c>CREATE SCHEMA IF NOT EXISTS</c>; the name is trimmed, its case kept).</summary>
    /// <param name="schema">Schema name.</param>
    /// <param name="ct">Cancellation token.</param>
    Task CreateSchemaAsync(string schema, CancellationToken ct = default);
}
