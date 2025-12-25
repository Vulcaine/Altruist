using Altruist.Contracts;
using Altruist.Migrations;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Altruist.Persistence.Postgres;

[ServiceConfiguration]
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


        var vaultModelTypes = PostgresVaultSetup.Configure(services, assemblies);
        var prefabModelTypes = PostgresPrefabSetup.Configure(services, assemblies);
        RegisterTransactionalServices(services, assemblies);

        var allModelTypes = vaultModelTypes
            .Concat(prefabModelTypes)
            .Distinct()
            .ToArray();

        await BootstrapOnceAsync(
            services,
            allModelTypes,
            initializerTypes,
            logPrefix: "Postgres").ConfigureAwait(false);

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

        // Create schemas used by both vaults + prefabs
        var schemaNames = modelTypes
            .Select(GetSchemaName)
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Select(n => n.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        foreach (var schemaName in schemaNames)
            await provider.CreateSchemaAsync(schemaName, null).ConfigureAwait(false);

        // Migrate once for all models
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
