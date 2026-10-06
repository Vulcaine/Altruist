using Altruist.Contracts;
using Altruist.Migrations;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using Npgsql;

namespace Altruist.Persistence.Postgres;

[ServiceConfiguration(order: -100)]
[ConditionalOnConfig("altruist:persistence:database:provider", havingValue: "postgres")]
public sealed class PostgresDatabaseConfiguration : PostgresConfigurationBase, IDatabaseConfiguration
{
    public bool IsConfigured { get; set; }

    public string DatabaseName => "PostgreSQL";

    public async Task Configure(IServiceCollection services)
    {
        var cfg = AppConfigLoader.Load();
        var assemblies = DiscoverAssemblies();

        var schemaTypes = FindSchemaTypes(assemblies).ToArray();
        var initializerTypes = FindInitializers(assemblies).ToArray();

        RegisterSchemasOnce(services, cfg, schemaTypes);
        RegisterNpgsqlDataSource(services, cfg);

        // Vaults are persisted + migrated
        var vaultModelTypes = PostgresVaultSetup.Configure(services, assemblies);

        // Prefabs are NOT persisted (no prefab tables) -> do not participate in migrations/bootstrap.
        // Prefab services are registered via PostgresPrefabsConfiguration (separate [ServiceConfiguration]).

        RegisterTransactionalServices(services, assemblies);

        var allModelTypes = vaultModelTypes
            .Distinct()
            .ToArray();

        Console.Error.WriteLine($"[PG-BOOTSTRAP] Bootstrapping {allModelTypes.Length} model types...");
        foreach (var mt in allModelTypes)
            Console.Error.WriteLine($"[PG-BOOTSTRAP]   {mt.Name}");

        await BootstrapOnceAsync(
            services,
            allModelTypes,
            initializerTypes,
            logPrefix: "Postgres").ConfigureAwait(false);

        Console.WriteLine("[PG-BOOTSTRAP] Done.");
        IsConfigured = true;
    }

    private static void RegisterSchemasOnce(
        IServiceCollection services,
        IConfiguration cfg,
        Type[] schemaTypes)
    {
        if (schemaTypes.Length == 0)
            return;

        if (services.Any(d => d.ServiceType == typeof(PostgresSchemaRegistrationMarker)))
            return;

        services.AddSingleton<PostgresSchemaRegistrationMarker>();

        using var tmp = services.BuildServiceProvider();
        var loggerFactory = tmp.GetService<ILoggerFactory>() ?? NullLoggerFactory.Instance;
        var logger = loggerFactory.CreateLogger(nameof(PostgresDatabaseConfiguration));

        RegisterSchemas(services, cfg, schemaTypes, logger);
    }

    private sealed class PostgresSchemaRegistrationMarker { }

    private static async Task BootstrapOnceAsync(
        IServiceCollection services,
        Type[] modelTypes,
        Type[] initializerTypes,
        string logPrefix)
    {
        if (services.Any(d => d.ServiceType == typeof(PostgresBootstrapMarker)))
            return;

        services.AddSingleton<PostgresBootstrapMarker>();

        using var sp = services.BuildServiceProvider();

        var loggerFactory = sp.GetService<ILoggerFactory>() ?? NullLoggerFactory.Instance;
        var logger = loggerFactory.CreateLogger(logPrefix);

        var provider = sp.GetService<ISqlDatabaseProvider>();
        var migrator = sp.GetService<IVaultSchemaMigrator>();

        if (provider is null)
        {
            logger.LogWarning("⚠️ No ISqlDatabaseProvider registered; skipping bootstrap.");
            return;
        }

        if (migrator is null)
        {
            logger.LogWarning("⚠️ No IVaultSchemaMigrator registered; skipping schema migration.");
            return;
        }

        // Connect once
        await provider.ConnectAsync().ConfigureAwait(false);

        // Servers of a fleet often start together against one database: one bootstrap at a time
        // (the others wait, then find the schema migrated), or concurrent CREATE SCHEMA / ALTER
        // TABLE statements collide.
        await using var bootstrapLock = await BootstrapLock.AcquireAsync(provider, logger).ConfigureAwait(false);

        // Create schemas used by persisted models (vaults).
        var schemaNames = modelTypes
            .Select(GetSchemaName)
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Select(n => n.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        foreach (var schemaName in schemaNames)
            await provider.CreateSchemaAsync(schemaName).ConfigureAwait(false);

        // Migrate once for persisted models
        if (modelTypes.Length > 0)
            await migrator.Migrate(modelTypes).ConfigureAwait(false);
        else
            logger.LogInformation("ℹ️ No model types found; skipping schema migration.");

        // Run initializers once
        await RunInitializersAsync(sp, initializerTypes, logger).ConfigureAwait(false);

        logger.LogInformation(
            "🐘 PostgreSQL bootstrap complete. {Count} model(s), {Init} initializer(s).",
            modelTypes.Length,
            initializerTypes.Length);
    }

    private sealed class PostgresBootstrapMarker { }

    /// <summary>
    /// A session-level Postgres advisory lock (per database) held for the whole bootstrap on a
    /// connection of its own; released when disposed (or when the connection drops).
    /// </summary>
    internal sealed class BootstrapLock : IAsyncDisposable
    {
        /// <summary>Fixed key shared by every Altruist server ("ALTRBOOT").</summary>
        public const long Key = 0x414C5452424F4F54;

        private readonly NpgsqlConnection? _conn;

        private BootstrapLock(NpgsqlConnection? conn) => _conn = conn;

        public static async Task<BootstrapLock> AcquireAsync(ISqlDatabaseProvider provider, ILogger logger)
        {
            if (provider is not GeneralSqlDatabaseProvider sql)
                return new BootstrapLock(null);
            var conn = new NpgsqlConnection(sql.GetConnectionString());
            try
            {
                await conn.OpenAsync().ConfigureAwait(false);
                await using (var probe = new NpgsqlCommand("SELECT pg_try_advisory_lock(@k)", conn))
                {
                    probe.Parameters.AddWithValue("k", Key);
                    if (await probe.ExecuteScalarAsync().ConfigureAwait(false) is true)
                        return new BootstrapLock(conn);
                }
                logger.LogInformation("⏳ Another server is bootstrapping this database; waiting for it.");
                await using var wait = new NpgsqlCommand("SELECT pg_advisory_lock(@k)", conn) { CommandTimeout = 0 };
                wait.Parameters.AddWithValue("k", Key);
                await wait.ExecuteNonQueryAsync().ConfigureAwait(false);
                return new BootstrapLock(conn);
            }
            catch
            {
                await conn.DisposeAsync().ConfigureAwait(false);
                throw;
            }
        }

        public async ValueTask DisposeAsync()
        {
            if (_conn is null) return;
            try
            {
                await using var unlock = new NpgsqlCommand("SELECT pg_advisory_unlock(@k)", _conn);
                unlock.Parameters.AddWithValue("k", Key);
                await unlock.ExecuteNonQueryAsync().ConfigureAwait(false);
            }
            catch (NpgsqlException) { /* closing the session releases it anyway */ }
            await _conn.DisposeAsync().ConfigureAwait(false);
        }
    }

    private static async Task RunInitializersAsync(
        IServiceProvider sp,
        Type[] initializerTypes,
        ILogger logger)
    {
        if (initializerTypes is null || initializerTypes.Length == 0)
            return;

        var ordered = initializerTypes
            .Select(t => ActivatorUtilities.CreateInstance(sp, t))
            .Cast<IDatabaseInitializer>()
            .OrderBy(i => i.Order)
            .ThenBy(i => i.GetType().FullName, StringComparer.Ordinal)
            .ToList();

        foreach (var init in ordered)
        {
            try
            {
                await init.InitializeAsync(sp).ConfigureAwait(false);
                logger.LogInformation("✔ Initializer {Init} executed.", init.GetType().Name);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "❌ Initializer {Init} failed: {Message}", init.GetType().Name, ex.Message);
            }
        }
    }
}
