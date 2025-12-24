using System.Reflection;

using Altruist.Gaming.Prefabs;
using Altruist.UORM;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Altruist.Persistence.Postgres;

[ConditionalOnConfig("altruist:persistence:database:provider", havingValue: "postgres")]
public sealed class PostgresServiceFactory : IServiceFactory
{
    public bool CanCreate(Type serviceType)
    {
        if (!serviceType.IsGenericType)
            return false;

        var genDef = serviceType.GetGenericTypeDefinition();

        if (genDef != typeof(IVault<>) && genDef != typeof(IPrefabVault<>))
            return false;

        var modelType = serviceType.GetGenericArguments()[0];

        if (!typeof(IVaultModel).IsAssignableFrom(modelType) && !typeof(IPrefabModel).IsAssignableFrom(modelType))
            return false;

        // Only models with [Vault] or [Prefab] are supported
        var hasVaultAttr = modelType.GetCustomAttribute<VaultAttribute>() != null;
        var hasPrefabAttr = modelType.GetCustomAttribute<PrefabAttribute>() != null;

        return hasVaultAttr || hasPrefabAttr;
    }

    public object Create(IServiceProvider sp, Type serviceType)
    {
        if (!CanCreate(serviceType))
            throw new InvalidOperationException($"PostgresServiceFactory cannot create {serviceType}.");

        var modelType = serviceType.GetGenericArguments()[0];

        var sqlProvider = sp.GetRequiredService<ISqlDatabaseProvider>();
        var loggerFactory = sp.GetService<ILoggerFactory>();

        var schemaName = GetSchemaName(modelType);
        var keyspace = sp.GetServices<IKeyspace>()
                         .FirstOrDefault(k => k.Name == schemaName)
                  ?? new DefaultSchema(schemaName);

        var doc = Document.From(modelType);

        var isPrefab = typeof(IPrefabModel).IsAssignableFrom(modelType);

        if (isPrefab)
        {
            var prefabVaultType = typeof(PgPrefabVault<>).MakeGenericType(modelType);
            return Activator.CreateInstance(prefabVaultType, sqlProvider, keyspace, doc)!;
        }
        else
        {
            var vaultType = typeof(PgVault<>).MakeGenericType(modelType);
            return Activator.CreateInstance(vaultType, sqlProvider, keyspace, doc)!;
        }
    }

    private static string GetSchemaName(Type modelType)
    {
        var va = modelType.GetCustomAttribute<VaultAttribute>();
        return string.IsNullOrWhiteSpace(va?.Keyspace) ? "public" : va!.Keyspace!;
    }
}
