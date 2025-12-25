// PostgresPrefabConfiguration.cs
using System.Reflection;

using Altruist.Contracts;
using Altruist.UORM;

using Microsoft.Extensions.DependencyInjection;

namespace Altruist.Persistence.Postgres;

/// <summary>
/// PostgreSQL configuration for prefab models.
/// Only types with IPrefabModel + a VaultAttribute-derived header ([Prefab], etc.) are handled here.
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

        EnsureSchemasRegistered(services, cfg, schemaTypes, loggerName: nameof(PostgresPrefabConfiguration));

        if (prefabTypes.Length > 0)
        {
            foreach (var prefabType in prefabTypes)
                PrefabMetadataRegistry.RegisterPrefab(prefabType);

            RegisterPrefabVaultsViaServiceFactory(services, prefabTypes);
            RegisterPrefabVaultAliases(services, prefabTypes);
        }

        DatabaseBootstrapCoordinator.AddModels(prefabTypes);
        DatabaseBootstrapCoordinator.AddInitializers(initializerTypes);
        DatabaseBootstrapCoordinator.MarkPrefabConfigured();

        await DatabaseBootstrapCoordinator.TryBootstrapOnceAsync(services, logPrefix: "Postgres").ConfigureAwait(false);

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
