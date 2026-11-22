// SqlDbProvider.cs (Postgres concrete)
using System.Data.Common;
using System.Text.Json;

using Altruist.Contracts;

using Npgsql;

using NpgsqlTypes;

namespace Altruist.Persistence.Postgres;

/// <summary>
/// Default (unkeyed) Npgsql-backed <see cref="ISqlDatabaseProvider"/>: the raw-SQL layer that every Postgres vault,
/// prefab and join query runs on. Inject <see cref="ISqlDatabaseProvider"/> to run hand-written SQL.
/// </summary>
/// <remarks>
/// <para>
/// Registered as a singleton for both <see cref="ISqlDatabaseProvider"/> and <see cref="IGeneralDatabaseProvider"/>
/// when <c>altruist:persistence:database:provider</c> is <c>postgres</c>. Configuration keys (under
/// <c>altruist:persistence:database</c>): <c>host</c> (default <c>localhost</c>), <c>port</c> (5432),
/// <c>username</c>, <c>password</c>, <c>database</c> (required), <c>pooling</c> (true), <c>max-pool-size</c> (300),
/// <c>ssl-mode</c> (<c>disable</c> | <c>allow</c> | <c>prefer</c> | <c>require</c> | <c>verify-ca</c> | <c>verify-full</c>;
/// any other value throws). Host, username and database are trimmed (their case is kept: quoted Postgres names are
/// case-sensitive); the password is used verbatim. The session time zone is forced to UTC.
/// </para>
/// <para>
/// When to use: prefer <c>IVault&lt;T&gt;</c> for single-table CRUD and <see cref="IPrefabs"/> for aggregates; use this
/// provider for SQL those layers cannot express. Write <c>?</c> placeholders (rewritten to <c>@p1</c>, <c>@p2</c>, ...
/// outside quoted text) and pass values positionally. Enums bind as <see cref="int"/>; types the base class classifies
/// as JSON are serialized with the injected <see cref="System.Text.Json.JsonSerializerOptions"/> and bound as
/// <c>jsonb</c>. Each call uses its own pooled connection unless an ambient transaction is active
/// (<see cref="ISqlTransactionProvider.InTransactionAsync{T}"/>, <see cref="TransactionalDecorator{T}"/>), in which
/// case it runs on that transaction's connection.
/// </para>
/// <para>
/// For additional named databases see <see cref="PgSqlDbInstanceProvider"/>.
/// </para>
/// </remarks>
/// <example>
/// <code>
/// // appsettings.json
/// // "altruist": { "persistence": { "database": {
/// //     "provider": "postgres", "host": "db", "port": 5432,
/// //     "username": "app", "password": "secret", "database": "app" } } }
///
/// var n = await sql.ExecuteCountAsync(
///     "SELECT COUNT(*) FROM \"altruist\".\"score\" WHERE \"points\" &gt; ?",
///     new List&lt;object?&gt; { 100 });
/// </code>
/// </example>
[Service(typeof(ISqlDatabaseProvider))]
[Service(typeof(IGeneralDatabaseProvider))]
[ConditionalOnConfig("altruist:persistence:database:provider", havingValue: "postgres")]
public sealed class PgSqlDbProvider : GeneralSqlDatabaseProvider
{
    private readonly string _host;
    private readonly int _port;
    private readonly string _username;
    private readonly string _password;
    private readonly string _database;

    private readonly bool _pooling;
    private readonly int _maxPoolSize;
    private readonly SslMode _sslMode;

    /// <inheritdoc/>
    public override string ServiceName { get; } = "PostgreSQL";
    /// <inheritdoc/>
    public override IDatabaseServiceToken Token { get; } = PostgresDBToken.Instance;

    /// <inheritdoc/>
    protected override string ParameterPrefix => "@";

    private static SslMode ParseSslMode(string rawLower) => rawLower switch
    {
        "" or "disable" => SslMode.Disable,
        "allow" => SslMode.Allow,
        "prefer" => SslMode.Prefer,
        "require" => SslMode.Require,
        "verifyca" or "verify-ca" => SslMode.VerifyCA,
        "verifyfull" or "verify-full" => SslMode.VerifyFull,
        _ => throw new ArgumentException($"Unknown ssl-mode '{rawLower}'; use disable, allow, prefer, require, verify-ca or verify-full.")
    };

    /// <summary>
    /// Creates the provider from configuration (values are injected from the keys named in the attributes).
    /// Does not connect; connection happens on first use or on <c>ConnectAsync</c>.
    /// </summary>
    /// <param name="jsonOptions">Serializer options for JSON/jsonb parameters and columns.</param>
    /// <param name="host"><c>altruist:persistence:database:host</c>; empty means <c>localhost</c>.</param>
    /// <param name="port"><c>altruist:persistence:database:port</c>; values &lt;= 0 mean 5432.</param>
    /// <param name="username"><c>altruist:persistence:database:username</c> (required).</param>
    /// <param name="password"><c>altruist:persistence:database:password</c> (required, may be empty).</param>
    /// <param name="database"><c>altruist:persistence:database:database</c> (required).</param>
    /// <param name="pooling"><c>altruist:persistence:database:pooling</c>.</param>
    /// <param name="maxPoolSize"><c>altruist:persistence:database:max-pool-size</c>.</param>
    /// <param name="sslMode"><c>altruist:persistence:database:ssl-mode</c>.</param>
    /// <exception cref="ArgumentNullException">Username or database is empty, or password is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="sslMode"/> is not a known mode.</exception>
    public PgSqlDbProvider(
        JsonSerializerOptions jsonOptions,
        [AppConfigValue("altruist:persistence:database:host")] string host,
        [AppConfigValue("altruist:persistence:database:port", "5432")] int port,
        [AppConfigValue("altruist:persistence:database:username")] string username,
        [AppConfigValue("altruist:persistence:database:password")] string password,
        [AppConfigValue("altruist:persistence:database:database")] string database,
        [AppConfigValue("altruist:persistence:database:pooling", "true")] bool pooling = true,
        [AppConfigValue("altruist:persistence:database:max-pool-size", "300")] int maxPoolSize = 300,
        [AppConfigValue("altruist:persistence:database:ssl-mode", "disable")] string sslMode = "disable")
        : base(jsonOptions)
    {
        _maxPoolSize = maxPoolSize;
        _host = string.IsNullOrWhiteSpace(host) ? "localhost" : host.Trim();
        _port = port <= 0 ? 5432 : port;

        _username = string.IsNullOrWhiteSpace(username) ? throw new ArgumentNullException(nameof(username)) : username.Trim();
        _database = string.IsNullOrWhiteSpace(database) ? throw new ArgumentNullException(nameof(database)) : database.Trim();

        _password = password ?? throw new ArgumentNullException(nameof(password));
        _pooling = pooling;
        _sslMode = ParseSslMode(NormLower(sslMode));
    }

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
            MaxPoolSize = _maxPoolSize,
            SslMode = _sslMode,
            // Deterministic timestamp conversions regardless of the server's default zone.
            Timezone = "UTC",
        };

        // NOTE: Do NOT set TrustServerCertificate; Npgsql marks it obsolete/no-op now.
        return csb.ConnectionString;
    }

    /// <inheritdoc/>
    /// <remarks>Creates an <see cref="NpgsqlConnection"/>.</remarks>
    protected override DbConnection CreateConnection(string connectionString)
        => new NpgsqlConnection(connectionString);

    /// <inheritdoc/>
    /// <remarks>Same rules as the base class, but JSON values are typed as <see cref="NpgsqlDbType.Jsonb"/>.</remarks>
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
            // Provider-specific JSONB
            if (p is NpgsqlParameter npg)
                npg.NpgsqlDbType = NpgsqlDbType.Jsonb;

            p.Value = JsonSerializer.Serialize(value, JsonOptions);
            return;
        }

        p.Value = value;
    }
}
