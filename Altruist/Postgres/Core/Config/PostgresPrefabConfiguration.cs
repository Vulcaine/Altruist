using System.Reflection;

using Altruist.UORM;

using Microsoft.Extensions.DependencyInjection;

namespace Altruist.Persistence.Postgres;

internal static class PostgresPrefabSetup
{
    public static Type[] Configure(IServiceCollection services, Assembly[] assemblies)
    {
        var prefabTypes = FindPrefabModelTypes(assemblies).ToArray();

        if (prefabTypes.Length == 0)
            return prefabTypes;

        foreach (var prefabType in prefabTypes)
            PrefabMetadataRegistry.RegisterPrefab(prefabType);

        RegisterPrefabVaultsViaServiceFactory(services, prefabTypes);
        RegisterPrefabVaultAliases(services, prefabTypes);

        return prefabTypes;
    }

    private static IEnumerable<Type> FindPrefabModelTypes(Assembly[] assemblies) =>
        assemblies
            .SelectMany(a => a.GetTypes())
            .Where(t =>
                t is not null &&
                t.IsClass &&
                !t.IsAbstract &&
                typeof(IPrefabModel).IsAssignableFrom(t) &&
                t.GetCustomAttribute<VaultAttribute>(inherit: true) is not null);

    private static void RegisterPrefabVaultsViaServiceFactory(IServiceCollection services, Type[] prefabTypes)
    {
        foreach (var prefabType in prefabTypes)
        {
            var schemaName = GetSchemaName(prefabType);

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

    private static string GetSchemaName(Type modelType)
    {
        var va = modelType.GetCustomAttribute<VaultAttribute>(inherit: false);
        if (!string.IsNullOrWhiteSpace(va?.Keyspace))
            return va!.Keyspace!.Trim();

        return "public";
    }
}
