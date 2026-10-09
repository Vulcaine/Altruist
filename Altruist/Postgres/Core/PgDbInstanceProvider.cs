// PgDbInstanceProvider.cs — Multi-instance Postgres provider
using System.Data.Common;
using System.Text.Json;

using Altruist.Contracts;

using Npgsql;

using NpgsqlTypes;

namespace Altruist.Persistence.Postgres;

/// <summary>
/// Named Postgres database instance, registered once per item in the
/// <c>altruist:persistence:database:instances</c> config array. Each instance is registered as a keyed
/// <see cref="ISqlDatabaseProvider"/> singleton under its <c>name</c> field.
/// </summary>
/// <remarks>
/// <para>
/// Use it to place some vaults on another database (e.g. a read replica or a separate store): set
/// <c>DbInstance</c> on the model's <c>[Vault]</c> attribute to the instance name and
/// <see cref="PostgresServiceFactory"/> will build that vault on this provider. If no instance with that name is
/// registered the vault silently uses the unkeyed <see cref="ISqlDatabaseProvider"/>. To run raw SQL on an instance,
/// resolve <see cref="ISqlDatabaseProvider"/> as a keyed service under the instance name.
/// </para>
/// <para>
/// Keys per item (relative to the item): <c>name</c>, <c>host</c>, <c>port</c> (5432), <c>username</c>,
/// <c>password</c>, <c>database</c>, <c>role</c> (<c>readwrite</c> | <c>readonly</c>, default <c>readwrite</c>),
/// <c>pooling</c> (true), <c>ssl-mode</c> (<c>disable</c>). There is no <c>max-pool-size</c> key here (Npgsql's default
/// applies). <see cref="Role"/> is informational only: nothing in this provider blocks writes on a
/// <c>readonly</c> instance. Startup schema creation and migration run only on the provider that resolves for an
/// unkeyed <see cref="ISqlDatabaseProvider"/>; note that list-registered instances are also registered unkeyed, so
/// which provider that is depends on registration order.
/// </para>
/// </remarks>
/// <example>
/// <code>
/// // "altruist": { "persistence": { "database": {
/// //     "provider": "postgres", "host": "db", "username": "app", "password": "s", "database": "app",
/// //     "instances": [ { "name": "replica", "host": "db-ro", "username": "app", "password": "s",
/// //                      "database": "app", "role": "readonly" } ] } } }
///
/// [Vault("report", DbInstance: "replica")]
/// public class ReportVault : VaultModel { /* ... */ }
/// </code>
/// </example>
[Service(typeof(ISqlDatabaseProvider))]
[ConditionalOnConfig("altruist:persistence:database:provider", havingValue: "postgres")]
[ConditionalOnConfig("altruist:persistence:database:instances", KeyField = "name")]
public sealed class PgSqlDbInstanceProvider : GeneralSqlDatabaseProvider
{
    private readonly string _name;
    private readonly string _role;
    private readonly string _host;
    private readonly int _port;
    private readonly string _username;
    private readonly string _password;
    private readonly string _database;
    private readonly bool _pooling;
    private readonly string _sslModeRaw;

    /// <inheritdoc/>
    /// <remarks><c>"PostgreSQL (&lt;name&gt;)"</c>.</remarks>
    public override string ServiceName { get; }
    /// <inheritdoc/>
    public override IDatabaseServiceToken Token { get; } = PostgresDBToken.Instance;
    /// <inheritdoc/>
    protected override string ParameterPrefix => "@";

    /// <summary>Instance name from config (e.g. "primary", "replica").</summary>
    public string Name => _name;

    /// <summary>Role from config, lower-cased: <c>"readwrite"</c> or <c>"readonly"</c> (informational; not enforced).</summary>
    public string Role => _role;

    /// <summary>Creates the provider for one <c>instances</c> item. Does not connect.</summary>
    /// <param name="jsonOptions">Serializer options for JSON/jsonb parameters and columns.</param>
    /// <param name="name">Item <c>name</c>; the DI key.</param>
    /// <param name="host">Item <c>host</c>; empty means <c>localhost</c>.</param>
    /// <param name="port">Item <c>port</c>; values &lt;= 0 mean 5432.</param>
    /// <param name="username">Item <c>username</c> (required, lower-cased).</param>
    /// <param name="password">Item <c>password</c> (required).</param>
    /// <param name="database">Item <c>database</c> (required, lower-cased).</param>
    /// <param name="role">Item <c>role</c>.</param>
    /// <param name="pooling">Item <c>pooling</c>.</param>
    /// <param name="sslMode">Item <c>ssl-mode</c>.</param>
    /// <exception cref="ArgumentNullException">Name, username or database is missing, or password is null.</exception>
    public PgSqlDbInstanceProvider(
        JsonSerializerOptions jsonOptions,
        [AppConfigValue("*:name")] string name,
        [AppConfigValue("*:host")] string host,
        [AppConfigValue("*:port", "5432")] int port,
        [AppConfigValue("*:username")] string username,
        [AppConfigValue("*:password")] string password,
        [AppConfigValue("*:database")] string database,
        [AppConfigValue("*:role", "readwrite")] string role = "readwrite",
        [AppConfigValue("*:pooling", "true")] bool pooling = true,
        [AppConfigValue("*:ssl-mode", "disable")] string sslMode = "disable")
        : base(jsonOptions)
    {
        _name = name ?? throw new ArgumentNullException(nameof(name));
        _role = NormLower(role);
        ServiceName = $"PostgreSQL ({_name})";

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
    }

    private static SslMode ParseSslMode(string rawLower) => rawLower switch
    {
        "" or "disable" => SslMode.Disable,
        "allow" => SslMode.Allow,
        "prefer" => SslMode.Prefer,
        "require" => SslMode.Require,
        "verifyca" or "verify-ca" => SslMode.VerifyCA,
        "verifyfull" or "verify-full" => SslMode.VerifyFull,
        _ => SslMode.Disable
    };

    /// <inheritdoc/>
    protected override string BuildConnectionString(string? overrideHost = null, int? overridePort = null)
    {
        var csb = new NpgsqlConnectionStringBuilder
        {
            Host = overrideHost ?? _host,
            Port = overridePort ?? _port,
            Username = _username,
            Password = _password,
            Database = _database,
            Pooling = _pooling,
            SslMode = ParseSslMode(_sslModeRaw),
            // Deterministic timestamp conversions regardless of the server's default zone.
            Timezone = "UTC",
        };
        return csb.ConnectionString;
    }

    /// <inheritdoc/>
    protected override DbConnection CreateConnection(string connectionString)
        => new NpgsqlConnection(connectionString);

    /// <inheritdoc/>
    /// <remarks>JSON values are typed as <see cref="NpgsqlDbType.Jsonb"/>.</remarks>
    protected override void BindParameter(DbParameter p, object? value)
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
            if (p is NpgsqlParameter npg)
                npg.NpgsqlDbType = NpgsqlDbType.Jsonb;

            p.Value = JsonSerializer.Serialize(value, JsonOptions);
            return;
        }

        p.Value = value;
    }

    /// <summary>Runs <c>SET search_path TO "&lt;schema&gt;"</c> on one leased connection (see <see cref="PgSqlDbProvider.ChangeKeyspaceAsync(string, CancellationToken)"/>).</summary>
    /// <param name="schema">Schema name.</param>
    /// <param name="ct">Cancellation token.</param>
    public override async Task ChangeKeyspaceAsync(string schema, CancellationToken ct = default)
    {
        await EnsureConnectedAsync(ct).ConfigureAwait(false);
        await ExecuteAsync($"SET search_path TO \"{NormLower(schema)}\";", parameters: null, ct).ConfigureAwait(false);
    }
}
