using System.Reflection;

using Altruist.UORM;

using Microsoft.Extensions.DependencyInjection;

namespace Altruist.Persistence.Postgres;

internal static class PostgresVaultSetup
{
    public static Type[] Configure(IServiceCollection services, Assembly[] assemblies)
    {
        var vaultTypes = FindVaultModelTypes(assemblies).ToArray();
        RegisterVaultsViaServiceFactory(services, vaultTypes);
        return vaultTypes;
    }

    private static IEnumerable<Type> FindVaultModelTypes(Assembly[] assemblies) =>
        TypeDiscovery.FindTypesWithAttribute<VaultAttribute>(assemblies)
            .Where(t =>
                t is not null &&
                t.IsClass &&
                !t.IsAbstract &&
                typeof(IVaultModel).IsAssignableFrom(t))!;

    private static void RegisterVaultsViaServiceFactory(IServiceCollection services, Type[] vaultModelTypes)
    {
        foreach (var modelType in vaultModelTypes)
        {
            var schemaName = GetSchemaName(modelType);

            VaultRegistry.Register(modelType, schemaName);

            var vaultIface = typeof(IVault<>).MakeGenericType(modelType);

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

    private static string GetSchemaName(Type modelType)
    {
        var va = modelType.GetCustomAttribute<VaultAttribute>(inherit: false);
        if (!string.IsNullOrWhiteSpace(va?.Keyspace))
            return va!.Keyspace!.Trim();

        return "public";
    }
}
