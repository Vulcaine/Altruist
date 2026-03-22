// PgDbInstanceProvider.cs — Multi-instance Postgres provider
using System.Data.Common;
using System.Text.Json;

using Altruist.Contracts;

using Npgsql;

using NpgsqlTypes;

namespace Altruist.Persistence.Postgres;

/// <summary>
/// Named Postgres database instance, registered once per item in
/// altruist:persistence:database:instances config section.
/// Each instance is keyed by its "name" field.
/// </summary>
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

    public override string ServiceName { get; }
    public override IDatabaseServiceToken Token { get; } = PostgresDBToken.Instance;
    protected override string ParameterPrefix => "@";

    /// <summary>Instance name from config (e.g. "primary", "replica").</summary>
    public string Name => _name;

    /// <summary>Role: "readwrite" or "readonly".</summary>
    public string Role => _role;

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
        };
        return csb.ConnectionString;
    }

    protected override DbConnection CreateConnection(string connectionString)
        => new NpgsqlConnection(connectionString);

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

    public override async Task ChangeKeyspaceAsync(string schema, CancellationToken ct = default)
    {
        await EnsureConnectedAsync(ct).ConfigureAwait(false);
        await ExecuteAsync($"SET search_path TO \"{NormLower(schema)}\";", parameters: null, ct).ConfigureAwait(false);
    }
}
