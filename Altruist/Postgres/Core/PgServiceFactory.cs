// PostgresServiceFactory.cs

using System.Reflection;

using Altruist.Contracts;
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

        // Prefabs are NOT vault-backed anymore.
        if (genDef != typeof(IVault<>))
            return false;

        var modelType = serviceType.GetGenericArguments()[0];

        if (!typeof(IVaultModel).IsAssignableFrom(modelType))
            return false;

        // Only models with [Vault] (or derived) are supported by PgVault<T>.
        return modelType.GetCustomAttribute<VaultAttribute>() != null;
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

        var vaultType = typeof(PgVault<>).MakeGenericType(modelType);
        return Activator.CreateInstance(vaultType, sqlProvider, keyspace, doc)!;
    }

    private static string GetSchemaName(Type modelType)
    {
        var va = modelType.GetCustomAttribute<VaultAttribute>();
        return string.IsNullOrWhiteSpace(va?.Keyspace) ? "public" : va!.Keyspace!;
    }
}

public sealed class PostgresDBToken : IDatabaseServiceToken
{
    public static PostgresDBToken Instance { get; } = new();
    public string Description => "💾 Database: PostgreSQL";
}

public sealed class DefaultSchema : IKeyspace
{
    public DefaultSchema(string? name = null)
    {
        Name = string.IsNullOrWhiteSpace(name) ? "public" : name!;
    }

    public string Name { get; }
    public IDatabaseServiceToken DatabaseToken => PostgresDBToken.Instance;
}
