// PostgresServiceFactory.cs

using System.Reflection;

using Altruist.Contracts;
using Altruist.UORM;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Altruist.Persistence.Postgres;

/// <summary>
/// <see cref="IServiceFactory"/> that builds <see cref="PgVault{TVaultModel}"/> instances for every
/// closed <see cref="IVault{TVaultModel}"/> whose model type implements <see cref="IVaultModel"/> and
/// carries a <see cref="VaultAttribute"/>.
/// </summary>
/// <remarks>
/// <para>
/// Active only when <c>altruist:persistence:database:provider</c> is <c>postgres</c>. You normally never
/// call it directly: Postgres configuration registers each discovered <c>[Vault]</c> model as a
/// singleton <c>IVault&lt;TModel&gt;</c> whose factory delegate asks the registered
/// <see cref="IServiceFactory"/> instances which one <see cref="CanCreate"/> it. Inject
/// <c>IVault&lt;TModel&gt;</c> instead.
/// </para>
/// <para>
/// Provider selection: when <see cref="VaultAttribute"/> names a <c>DbInstance</c>, the keyed
/// <see cref="ISqlDatabaseProvider"/> registered under that name is used (named instances come from
/// <c>altruist:persistence:database:instances</c>, see <see cref="PgSqlDbInstanceProvider"/>); an unknown name throws.
/// Otherwise the default provider (<see cref="PgSqlDbProvider"/>) is used.
/// </para>
/// <para>
/// Schema selection: <see cref="VaultAttribute.SchemaName"/> is matched by name against registered
/// <see cref="IKeyspace"/> services; when none matches a <see cref="DefaultSchema"/> with that name is used.
/// </para>
/// </remarks>
[ConditionalOnConfig("altruist:persistence:database:provider", havingValue: "postgres")]
public sealed class PostgresServiceFactory : IServiceFactory
{
    /// <inheritdoc/>
    /// <remarks>
    /// True only for closed <c>IVault&lt;TModel&gt;</c> where <c>TModel</c> implements
    /// <see cref="IVaultModel"/> and is directly annotated with <see cref="VaultAttribute"/> (or a subclass of it).
    /// </remarks>
    public bool CanCreate(Type serviceType)
    {
        if (!serviceType.IsGenericType)
            return false;

        var genDef = serviceType.GetGenericTypeDefinition();

        if (genDef != typeof(IVault<>))
            return false;

        var modelType = serviceType.GetGenericArguments()[0];

        if (!typeof(IVaultModel).IsAssignableFrom(modelType))
            return false;

        return modelType.GetCustomAttribute<VaultAttribute>() != null;
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Returns a new <see cref="PgVault{TVaultModel}"/> on every call (the DI registration made by the
    /// Postgres configuration is a singleton, so in practice one vault exists per model type).
    /// </remarks>
    /// <exception cref="InvalidOperationException"><paramref name="serviceType"/> is not supported (see <see cref="CanCreate"/>), or the model's <c>DbInstance</c> is not a configured instance.</exception>
    public object Create(IServiceProvider sp, Type serviceType)
    {
        if (!CanCreate(serviceType))
            throw new InvalidOperationException($"PostgresServiceFactory cannot create {serviceType}.");

        var modelType = serviceType.GetGenericArguments()[0];
        var loggerFactory = sp.GetService<ILoggerFactory>();

        var va = modelType.GetCustomAttribute<VaultAttribute>()!;

        var sqlProvider = ResolveProvider(sp, modelType, va.DbInstance);

        var schemaName = va.SchemaName;

        var keyspace = sp.GetServices<IKeyspace>()
                         .FirstOrDefault(k => k.Name == schemaName)
                  ?? new DefaultSchema(schemaName);

        var doc = VaultDocument.From(modelType);

        var vaultType = typeof(PgVault<>).MakeGenericType(modelType);
        return Activator.CreateInstance(vaultType, sqlProvider, keyspace, doc)!;
    }

    /// <summary>
    /// The provider a model's vault runs on: the keyed instance named by <paramref name="dbInstance"/>, or the default
    /// <see cref="PgSqlDbProvider"/> when it is blank.
    /// </summary>
    /// <param name="sp">Service provider.</param>
    /// <param name="modelType">The vault model (for the error message).</param>
    /// <param name="dbInstance">The model's <c>DbInstance</c>.</param>
    /// <returns>The provider.</returns>
    /// <exception cref="InvalidOperationException">No instance is configured under <paramref name="dbInstance"/>.</exception>
    public static ISqlDatabaseProvider ResolveProvider(IServiceProvider sp, Type modelType, string? dbInstance)
    {
        if (string.IsNullOrWhiteSpace(dbInstance))
            return sp.GetRequiredService<PgSqlDbProvider>();

        return sp.GetKeyedService<ISqlDatabaseProvider>(dbInstance)
            ?? throw new InvalidOperationException(
                $"{modelType.Name} uses DbInstance '{dbInstance}', but no database instance with that name is configured " +
                "under altruist:persistence:database:instances.");
    }
}

/// <summary>
/// Service token identifying the PostgreSQL database provider. Returned by the Postgres providers'
/// <c>Token</c> and by <see cref="DefaultSchema.DatabaseToken"/>; use <see cref="Instance"/>, never construct one.
/// </summary>
public sealed class PostgresDBToken : IDatabaseServiceToken
{
    /// <summary>The shared singleton token.</summary>
    public static PostgresDBToken Instance { get; } = new();
    /// <summary>Human-readable label: <c>"Database: PostgreSQL"</c>.</summary>
    public string Description => "Database: PostgreSQL";
}

/// <summary>
/// Minimal <see cref="IKeyspace"/> (a Postgres schema) used when a vault's schema has no registered
/// <c>[Keyspace]</c> class. Declare your own <see cref="IKeyspace"/> class with <see cref="KeyspaceAttribute"/>
/// instead when the schema needs configuration.
/// </summary>
public sealed class DefaultSchema : IKeyspace
{
    /// <summary>Creates a schema descriptor.</summary>
    /// <param name="name">Schema name; null or whitespace means <c>public</c>.</param>
    public DefaultSchema(string? name = null)
    {
        Name = string.IsNullOrWhiteSpace(name) ? "public" : name!;
    }

    /// <summary>Postgres schema name (never empty; defaults to <c>public</c>).</summary>
    public string Name { get; }
    /// <summary>Always <see cref="PostgresDBToken.Instance"/>.</summary>
    public IDatabaseServiceToken DatabaseToken => PostgresDBToken.Instance;
}
