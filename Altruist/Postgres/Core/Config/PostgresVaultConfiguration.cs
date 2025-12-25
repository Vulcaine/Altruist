// PostgresVaultConfiguration.cs
using System.Reflection;

using Altruist.Contracts;
using Altruist.UORM;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

using Npgsql;

namespace Altruist.Persistence.Postgres;

public sealed class PostgresDBToken : IDatabaseServiceToken
{
    public static PostgresDBToken Instance { get; } = new();
    public IDatabaseConfiguration Configuration => new PostgresVaultConfiguration();
    public string Description => "💾 Database: PostgreSQL";
}

[ServiceConfiguration]
[ConditionalOnConfig("altruist:persistence:database:provider", havingValue: "postgres")]
public sealed class PostgresVaultConfiguration : PostgresConfigurationBase, IDatabaseConfiguration
{
    public bool IsConfigured { get; set; }
    public string DatabaseName => "PostgreSQL";

    public async Task Configure(IServiceCollection services)
    {
        var cfg = AppConfigLoader.Load();
        var assemblies = DiscoverAssemblies();

        // Discover once (per config)
        var schemaTypes = FindSchemaTypes(assemblies).ToArray();
        var vaultModelTypes = FindVaultModelTypes(assemblies).ToArray();
        var initializerTypes = FindInitializers(assemblies).ToArray();

        // Register schemas ONCE globally (idempotent + guarded)
        EnsureSchemasRegistered(services, cfg, schemaTypes, loggerName: nameof(PostgresVaultConfiguration));

        // Register vaults + core pg services
        RegisterVaultsViaServiceFactory(services, vaultModelTypes);
        RegisterNpgsqlDataSource(services, cfg);
        RegisterTransactionalServices(services, assemblies);

        // Contribute types to global bootstrap coordinator and try to run bootstrap once
        DatabaseBootstrapCoordinator.AddModels(vaultModelTypes);
        DatabaseBootstrapCoordinator.AddInitializers(initializerTypes);
        DatabaseBootstrapCoordinator.MarkVaultConfigured();

        await DatabaseBootstrapCoordinator.TryBootstrapOnceAsync(services, logPrefix: "Postgres").ConfigureAwait(false);

        IsConfigured = true;
    }

    // ----------------- vault discovery -----------------

    private static IEnumerable<Type> FindVaultModelTypes(Assembly[] assemblies) =>
        TypeDiscovery.FindTypesWithAttribute<VaultAttribute>(assemblies)
            .Where(t =>
                t is not null &&
                t.IsClass &&
                !t.IsAbstract &&
                typeof(IVaultModel).IsAssignableFrom(t))!;

    // ----------------- IVault<T> registration -----------------

    private static void RegisterVaultsViaServiceFactory(
        IServiceCollection services,
        Type[] vaultModelTypes)
    {
        foreach (var modelType in vaultModelTypes)
        {
            var schemaName = GetSchemaName(modelType);

            // metadata only – no instance creation here
            VaultRegistry.Register(modelType, schemaName);

            var vaultIface = typeof(IVault<>).MakeGenericType(modelType);

            // The actual vault instance is created by an IServiceFactory (PostgresServiceFactory)
            services.AddSingleton(vaultIface, sp =>
            {
                var factories = sp.GetServices<IServiceFactory>().ToList();
                var factory = factories.FirstOrDefault(f => f.CanCreate(vaultIface));
                if (factory is null)
                {
                    throw new InvalidOperationException(
                        $"No IServiceFactory can create '{vaultIface}'. " +
                        "Did you reference the correct provider package and enable it via config?");
                }

                return factory.Create(sp, vaultIface);
            });
        }
    }

    // ----------------- Npgsql -----------------

    private static void RegisterNpgsqlDataSource(IServiceCollection services, IConfiguration cfg)
    {
        // Avoid double-registration if some other config already added it
        if (services.Any(d => d.ServiceType == typeof(NpgsqlDataSource)))
            return;

        var connString = cfg.GetConnectionString("postgres");

        if (string.IsNullOrWhiteSpace(connString))
        {
            var dbSection = cfg.GetSection("altruist:persistence:database");

            var provider = dbSection["provider"];
            if (!string.Equals(provider, "postgres", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"PostgresVaultConfiguration used but provider is '{provider ?? "<null>"}'.");
            }

            var host = dbSection["host"] ?? "localhost";
            var portStr = dbSection["port"] ?? "5432";
            var username = dbSection["username"];
            var password = dbSection["password"];
            var database = dbSection["database"];

            if (string.IsNullOrWhiteSpace(username) ||
                string.IsNullOrWhiteSpace(password) ||
                string.IsNullOrWhiteSpace(database))
            {
                throw new InvalidOperationException(
                    "PostgreSQL configuration is incomplete. " +
                    "Expected altruist:persistence:database:username, :password, :database.");
            }

            if (!int.TryParse(portStr, out var port))
                port = 5432;

            var builder = new NpgsqlConnectionStringBuilder
            {
                Host = host,
                Port = port,
                Username = username,
                Password = password,
                Database = database,
            };

            connString = builder.ConnectionString;
        }

        if (string.IsNullOrWhiteSpace(connString))
            throw new InvalidOperationException("PostgreSQL connection string not found in configuration.");

        services.AddSingleton(_ =>
        {
            var builder = new NpgsqlDataSourceBuilder(connString);
            return builder.Build();
        });
    }
}

// Shared default schema for vaults / prefabs
public sealed class DefaultSchema : IKeyspace
{
    public DefaultSchema(string? name = null)
    {
        Name = string.IsNullOrWhiteSpace(name) ? "public" : name!;
    }

    public string Name { get; }
    public IDatabaseServiceToken DatabaseToken => PostgresDBToken.Instance;
}
