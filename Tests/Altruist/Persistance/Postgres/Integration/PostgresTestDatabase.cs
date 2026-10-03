/*
Copyright 2025 Aron Gere
Licensed under the Apache License, Version 2.0
*/

using System.Text.Json;

using Altruist.Persistence.Postgres;

using Npgsql;

namespace Tests.Altruist.Persistance.Postgres.Integration;

/// <summary>
/// Where the Postgres integration tests run. The connection string comes from the
/// <c>ALTRUIST_TEST_PG</c> environment variable and must point at a server where the user may
/// create databases (the database named in it is only used for CREATE/DROP DATABASE). Each test
/// run creates its own throwaway database <c>altruist_test_&lt;random&gt;</c> and drops it afterwards;
/// nothing else on the server is touched.
///
/// <para>When the server is not reachable every integration test is reported as skipped (not
/// failed), unless <c>ALTRUIST_TEST_PG_REQUIRED=true</c> (set in CI), which turns that into a failure. Run only these tests with <c>dotnet test --filter Category=Integration</c>, or leave
/// them out with <c>--filter Category!=Integration</c>.</para>
/// </summary>
public static class PostgresTestEnvironment
{
    public const string EnvVar = "ALTRUIST_TEST_PG";

    /// <summary>Set to "true" (CI) to fail instead of skip when the server is unreachable.</summary>
    public const string RequiredEnvVar = "ALTRUIST_TEST_PG_REQUIRED";
    public const string Category = "Integration";

    /// <summary>A stock local Postgres (e.g. <c>docker run -e POSTGRES_PASSWORD=postgres -p 5432:5432 postgres:16</c>).</summary>
    public const string DefaultConnectionString =
        "Host=127.0.0.1;Port=5432;Username=postgres;Password=postgres;Database=postgres";

    private static readonly Lazy<string?> _unavailableReason = new(Probe, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>Null when Postgres is reachable; otherwise why the integration tests are skipped.</summary>
    public static string? UnavailableReason => _unavailableReason.Value;

    /// <summary>True when an unreachable server must fail the tests instead of skipping them.</summary>
    public static bool Required =>
        string.Equals(Environment.GetEnvironmentVariable(RequiredEnvVar), "true", StringComparison.OrdinalIgnoreCase);

    /// <summary>The skip reason for integration tests, or null when they must run (or fail).</summary>
    public static string? SkipReason => Required ? null : UnavailableReason;

    public static NpgsqlConnectionStringBuilder AdminConnection()
    {
        var raw = Environment.GetEnvironmentVariable(EnvVar);
        var csb = new NpgsqlConnectionStringBuilder(string.IsNullOrWhiteSpace(raw) ? DefaultConnectionString : raw);
        if (string.IsNullOrWhiteSpace(csb.Database))
            csb.Database = "postgres";
        if (csb.Timeout > 5 || csb.Timeout <= 0)
            csb.Timeout = 3;
        csb.Pooling = false;
        return csb;
    }

    private static string? Probe()
    {
        var csb = AdminConnection();
        try
        {
            using var conn = new NpgsqlConnection(csb.ConnectionString);
            conn.Open();
            using var cmd = new NpgsqlCommand("SELECT 1", conn);
            cmd.ExecuteScalar();
            return null;
        }
        catch (Exception ex)
        {
            return $"Postgres not reachable at {csb.Host}:{csb.Port} ({ex.GetType().Name}: {ex.Message}). " +
                   $"Set {EnvVar} to a connection string of a server where the user may CREATE DATABASE.";
        }
    }
}

/// <summary>A fact that needs Postgres: skipped (with the reason) when the server is unreachable.</summary>
public sealed class PostgresFactAttribute : FactAttribute
{
    public PostgresFactAttribute()
    {
        if (PostgresTestEnvironment.SkipReason is { } reason)
            Skip = reason;
    }
}

/// <summary>A theory that needs Postgres: skipped (with the reason) when the server is unreachable.</summary>
public sealed class PostgresTheoryAttribute : TheoryAttribute
{
    public PostgresTheoryAttribute()
    {
        if (PostgresTestEnvironment.SkipReason is { } reason)
            Skip = reason;
    }
}

/// <summary>
/// One throwaway database per test run (shared by the "postgres" collection), dropped on dispose.
/// </summary>
public sealed class PostgresDatabaseFixture : IAsyncLifetime
{
    /// <summary>Schema the integration vault models live in (inside the throwaway database).</summary>
    public const string Schema = "altruist_it";

    public bool Available { get; private set; }
    public string DatabaseName { get; } = $"altruist_test_{DateTime.UtcNow:yyyyMMddHHmmss}_{Guid.NewGuid().ToString("N")[..8]}";

    /// <summary>Connection string for the throwaway database.</summary>
    public string ConnectionString { get; private set; } = "";

    public PgSqlDbProvider Provider { get; private set; } = null!;
    public NpgsqlDataSource DataSource { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        if (PostgresTestEnvironment.UnavailableReason is { } reason)
        {
            if (PostgresTestEnvironment.Required)
                throw new InvalidOperationException($"{PostgresTestEnvironment.RequiredEnvVar}=true but: {reason}");
            return;
        }

        var admin = PostgresTestEnvironment.AdminConnection();
        await using (var conn = new NpgsqlConnection(admin.ConnectionString))
        {
            await conn.OpenAsync();
            await using var create = new NpgsqlCommand($"CREATE DATABASE \"{DatabaseName}\"", conn);
            await create.ExecuteNonQueryAsync();
        }

        var csb = new NpgsqlConnectionStringBuilder(admin.ConnectionString) { Database = DatabaseName, Pooling = true };
        ConnectionString = csb.ConnectionString;
        DataSource = NpgsqlDataSource.Create(ConnectionString);

        Provider = new PgSqlDbProvider(new JsonSerializerOptions(), csb.Host!, csb.Port, csb.Username!, csb.Password ?? "", DatabaseName);
        await Provider.ConnectAsync(3, 500);
        await Provider.CreateSchemaAsync(Schema);
        Available = true;
    }

    /// <summary>Statement through the provider (joins an ambient transaction when one is active).</summary>
    public Task<long> Exec(string sql) => Provider.ExecuteAsync(sql, (List<object?>?)null, CancellationToken.None);

    /// <summary>Scalar count through the provider (joins an ambient transaction when one is active).</summary>
    public Task<long> Count(string sql) => Provider.ExecuteCountAsync(sql, (List<object?>?)null, CancellationToken.None);

    /// <summary>A plain connection that is NOT bound to any ambient transaction (an independent observer).</summary>
    public async Task<NpgsqlConnection> OpenIndependentConnectionAsync()
    {
        var conn = new NpgsqlConnection(ConnectionString);
        await conn.OpenAsync();
        return conn;
    }

    /// <summary>Scalar query on an independent connection (sees only committed data).</summary>
    public async Task<object?> CommittedScalarAsync(string sql)
    {
        await using var conn = await OpenIndependentConnectionAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        return await cmd.ExecuteScalarAsync();
    }

    public async Task DisposeAsync()
    {
        if (!Available)
            return;

        try { await Provider.ShutdownAsync(null, CancellationToken.None); } catch { /* best effort */ }
        await DataSource.DisposeAsync();
        NpgsqlConnection.ClearAllPools();

        var admin = PostgresTestEnvironment.AdminConnection();
        await using var conn = new NpgsqlConnection(admin.ConnectionString);
        await conn.OpenAsync();
        await using var drop = new NpgsqlCommand($"DROP DATABASE IF EXISTS \"{DatabaseName}\" WITH (FORCE)", conn);
        await drop.ExecuteNonQueryAsync();
    }
}

[CollectionDefinition(Name)]
public sealed class PostgresCollection : ICollectionFixture<PostgresDatabaseFixture>
{
    public const string Name = "postgres";
}
