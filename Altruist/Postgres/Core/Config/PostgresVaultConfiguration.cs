using System.Reflection;

using Altruist.UORM;

using Microsoft.Extensions.DependencyInjection;

namespace Altruist.Persistence.Postgres;

/// <summary>
/// Startup helper used by <see cref="PostgresDatabaseConfiguration"/>: discovers every non-abstract
/// <see cref="IVaultModel"/> class annotated with <see cref="VaultAttribute"/>, records its schema in
/// <see cref="VaultRegistry"/>, and registers a <b>singleton</b> <c>IVault&lt;TModel&gt;</c> whose instance is
/// produced by the first registered <see cref="IServiceFactory"/> that can create it
/// (<see cref="PostgresServiceFactory"/> for Postgres).
/// </summary>
internal static class PostgresVaultSetup
{
    /// <summary>Discovers and registers the vaults; returns the discovered model types (used for migration).</summary>
    /// <param name="services">Service collection being configured.</param>
    /// <param name="assemblies">Assemblies to scan.</param>
    /// <returns>The vault model types found.</returns>
    public static Type[] Configure(IServiceCollection services, Assembly[] assemblies)
    {
        var vaultTypes = FindVaultModelTypes(assemblies).ToArray();
        Console.Error.WriteLine($"[VAULT-SETUP] Found {vaultTypes.Length} vault models in {assemblies.Length} assemblies");
        foreach (var vt in vaultTypes)
            Console.Error.WriteLine($"[VAULT-SETUP]   {vt.FullName}");
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
            var schemaName = modelType.GetCustomAttribute<VaultAttribute>(inherit: true)!.SchemaName;

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
}
