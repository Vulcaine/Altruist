using System.Reflection;

using Altruist.Contracts;
using Altruist.UORM;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Altruist.Persistence.Postgres;

/// <summary>
/// PostgreSQL configuration for prefab models.
/// Only types with [Prefab] + IPrefabModel are handled here.
/// </summary>
[ServiceConfiguration]
[ConditionalOnConfig("altruist:persistence:database:provider", havingValue: "postgres")]
public sealed class PostgresPrefabConfiguration : PostgresConfigurationBase, IDatabaseConfiguration
{
    public bool IsConfigured { get; set; }

    public string DatabaseName => "PostgreSQL Prefabs";

    public async Task Configure(IServiceCollection services)
    {
        var cfg = AppConfigLoader.Load();
        var assemblies = DiscoverAssemblies();

        var schemaTypes = FindSchemaTypes(assemblies).ToArray();
        var prefabTypes = FindPrefabModelTypes(assemblies).ToArray();
        var initializerTypes = FindInitializers(assemblies).ToArray();

        if (prefabTypes.Length == 0)
            return;

        // Register schemas (same as vault config; required if you rely on IKeyspace resolution via DI)
        using (var tmp = services.BuildServiceProvider())
        {
            var logger = tmp.GetRequiredService<ILoggerFactory>()
                            .CreateLogger<PostgresPrefabConfiguration>();
            RegisterSchemas(services, cfg, schemaTypes, logger);
        }

        // 1) Register prefab metadata so expression translator knows components.
        foreach (var prefabType in prefabTypes)
            PrefabMetadataRegistry.RegisterPrefab(prefabType);

        // 2) Register IVault<TPrefab> via IServiceFactory (PgPrefabVault<T> is chosen by PostgresServiceFactory)
        RegisterPrefabVaultsViaServiceFactory(services, prefabTypes);

        // 3) Register IPrefabVault<TPrefab> aliases
        RegisterPrefabVaultAliases(services, prefabTypes);

        // 4) Bootstrap *prefab models* (FIX: correct arrays, correct initializer list)
        await BootstrapModelsAsync(
            services,
            prefabTypes,
            initializerTypes,
            "Prefabs");

        IsConfigured = true;
    }

    // ----------------- prefab discovery -----------------

    private static IEnumerable<Type> FindPrefabModelTypes(Assembly[] assemblies) =>
    assemblies
        .SelectMany(a => a.GetTypes())
        .Where(t =>
            t is not null &&
            t.IsClass &&
            !t.IsAbstract &&
            typeof(IPrefabModel).IsAssignableFrom(t) &&
            t.GetCustomAttribute<VaultAttribute>(inherit: false) is not null);

    // ----------------- registration -----------------

    private static void RegisterPrefabVaultsViaServiceFactory(
        IServiceCollection services,
        Type[] prefabTypes)
    {
        foreach (var prefabType in prefabTypes)
        {
            var schemaName = GetSchemaName(prefabType);

            // Register as a model in VaultRegistry so Document / migrations know about it
            VaultRegistry.Register(prefabType, schemaName);

            var vaultIface = typeof(IVault<>).MakeGenericType(prefabType);

            services.AddSingleton(vaultIface, sp =>
            {
                var factories = sp.GetServices<IServiceFactory>().ToList();
                var factory = factories.FirstOrDefault(f => f.CanCreate(vaultIface));
                if (factory is null)
                {
                    throw new InvalidOperationException(
                        $"No IServiceFactory can create '{vaultIface}'. " +
                        "Did you reference the Postgres provider and enable it via config?");
                }

                return factory.Create(sp, vaultIface);
            });
        }
    }

    private static void RegisterPrefabVaultAliases(IServiceCollection services, Type[] prefabTypes)
    {
        foreach (var prefabType in prefabTypes)
        {
            var vaultIface = typeof(IVault<>).MakeGenericType(prefabType);
            var prefabVaultIface = typeof(IPrefabVault<>).MakeGenericType(prefabType);

            services.AddSingleton(prefabVaultIface, sp => sp.GetRequiredService(vaultIface));
        }
    }
}
