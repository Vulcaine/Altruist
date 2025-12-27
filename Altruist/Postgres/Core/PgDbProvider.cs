// SqlDbProvider.cs (Postgres concrete)
using System.Data.Common;
using System.Text.Json;

using Altruist.Contracts;

using Npgsql;

using NpgsqlTypes;

namespace Altruist.Persistence.Postgres;

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
    private readonly string _sslModeRaw;

    public override string ServiceName { get; } = "PostgreSQL";
    public override IDatabaseServiceToken Token { get; } = PostgresDBToken.Instance;

    protected override string ParameterPrefix => "@";

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

    public PgSqlDbProvider(
        JsonSerializerOptions jsonOptions,
        [AppConfigValue("altruist:persistence:database:host")] string host,
        [AppConfigValue("altruist:persistence:database:port", "5432")] int port,
        [AppConfigValue("altruist:persistence:database:username")] string username,
        [AppConfigValue("altruist:persistence:database:password")] string password,
        [AppConfigValue("altruist:persistence:database:database")] string database,
        [AppConfigValue("altruist:persistence:database:pooling", "true")] bool pooling = true,
        [AppConfigValue("altruist:persistence:database:ssl-mode", "disable")] string sslMode = "disable")
        : base(jsonOptions)
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
    }

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

        // NOTE: Do NOT set TrustServerCertificate; Npgsql marks it obsolete/no-op now.
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
            // Provider-specific JSONB
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

        // use base's internal connection via Query/Execute path OR keep local:
        // easiest is just execute SQL through ExecuteAsync:
        await ExecuteAsync($"SET search_path TO \"{NormLower(schema)}\";", parameters: null, ct).ConfigureAwait(false);
    }
}
