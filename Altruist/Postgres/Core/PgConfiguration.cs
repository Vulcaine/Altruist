using System.Collections;
using System.Reflection;

using Altruist.Contracts;
using Altruist.Persistence;
using Altruist.UORM;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using Npgsql;

namespace Altruist.Postgres;

public sealed class PostgresDBToken : IDatabaseServiceToken
{
    public static PostgresDBToken Instance { get; } = new PostgresDBToken();
    public IDatabaseConfiguration Configuration => new PostgresDBConfiguration();
    public string Description => "💾 Database: PostgreSQL";
}

public sealed class PostgresVaultRepository<TSchema> : VaultRepository<TSchema>
    where TSchema : class, IKeyspace
{
    public PostgresVaultRepository(IServiceProvider provider, ISqlDatabaseProvider databaseProvider, TSchema schema)
        : base(provider, databaseProvider, schema) { }
}

[ServiceConfiguration]
public sealed class PostgresDBConfiguration : IDatabaseConfiguration
{
    public string DatabaseName => "PostgreSQL";

    public async Task Configure(IServiceCollection services)
    {
        var cfg = AppConfigLoader.Load();
        var assemblies = DiscoverAssemblies();

        // 1) Discover schemas + vault models
        var schemaTypes = FindSchemaTypes(assemblies).ToArray();      // optional; falls back to DefaultSchema
        var vaultModelTypes = FindVaultModelTypes(assemblies).ToArray();

        // 2) Register schemas
        RegisterSchemas(services, cfg, schemaTypes);

        // 3) Let IServiceFactory handle IVault<T> creation; we only declare the services + metadata.
        RegisterVaultsViaServiceFactory(services, vaultModelTypes);

        // 4) Ensure NpgsqlDataSource is available (for transactional decorator)
        RegisterNpgsqlDataSource(services, cfg);

        // 5) Populate the TransactionalRegistry and wrap transactional services
        RegisterTransactionalServices(services, assemblies);

        // 6) Bootstrap DB objects
        await BootstrapAsync(services, schemaTypes, vaultModelTypes);
    }

    // ----------------- schemas -----------------

    private static void RegisterSchemas(IServiceCollection services, IConfiguration cfg, Type[] schemaTypes)
    {
        foreach (var schemaType in schemaTypes)
        {
            services.AddSingleton(schemaType, sp =>
            {
                var logger = sp.GetRequiredService<ILoggerFactory>().CreateLogger<PostgresDBConfiguration>();
                var inst = DependencyResolver.CreateWithConfiguration(sp, cfg, schemaType, logger);
                _ = DependencyResolver.InvokePostConstructAsync(inst, sp, cfg, logger);
                return inst!;
            });

            // Expose as IKeyspace (logical namespace)
            services.AddSingleton(typeof(IKeyspace), sp => (IKeyspace)sp.GetRequiredService(schemaType));
        }
    }

    // ----------------- vaults via IServiceFactory -----------------

    private static void RegisterVaultsViaServiceFactory(
        IServiceCollection services,
        Type[] vaultModelTypes)
    {
        foreach (var modelType in vaultModelTypes)
        {
            var schemaName = GetSchemaName(modelType); // from [Vault(Keyspace=...)] or defaults

            // metadata only – no instance creation here
            VaultRegistry.Register(modelType, schemaName);

            var vaultIface = typeof(IVault<>).MakeGenericType(modelType);

            // The *actual* vault instance will be created by whatever IServiceFactory
            // can handle this service type (e.g., PostgresServiceFactory).
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

    // ----------------- bootstrap -----------------

    private static async Task BootstrapAsync(IServiceCollection services, Type[] schemaTypes, Type[] vaultModelTypes)
    {
        // Build provider and KEEP it alive for the whole bootstrap (no fire-and-forget)
        using var sp = services.BuildServiceProvider();
        var logger = sp.GetRequiredService<ILoggerFactory>().CreateLogger<PostgresDBConfiguration>();

        var provider = sp.GetService<ISqlDatabaseProvider>();
        if (provider is null)
        {
            logger.LogWarning("⚠️ No ISqlDatabaseProvider registered; skipping PostgreSQL bootstrap.");
            return;
        }

        if (vaultModelTypes.Length == 0)
        {
            logger.LogInformation("ℹ️ No [Vault]/[Prefab]-annotated IVaultModel types found.");
            return;
        }

        var groups = vaultModelTypes.GroupBy(GetSchemaName);
        var allSchemaInstances = sp.GetServices<IKeyspace>().ToList();

        foreach (var group in groups)
        {
            var schemaName = group.Key;

            var schemaInstance = ResolveSchemaInstance(sp, schemaTypes, allSchemaInstances, schemaName)
                                 ?? new DefaultSchema(schemaName);

            await provider.ConnectAsync();
            await provider.CreateSchemaAsync(schemaInstance.Name, null);

            await CreateTablesAndRunHooksAsync(provider, sp, logger, schemaInstance, group.ToArray());
            await provider.ShutdownAsync();
        }

        logger.LogInformation(
            "🐘 PostgreSQL activated. {Count} vault model(s) registered and bootstrapped. ✨",
            vaultModelTypes.Length);
    }

    private static IKeyspace? ResolveSchemaInstance(
        IServiceProvider sp,
        Type[] schemaTypes,
        List<IKeyspace> allSchemaInstances,
        string schemaName)
    {
        var schemaInstance = allSchemaInstances.FirstOrDefault(s =>
            string.Equals(s.Name, schemaName, StringComparison.OrdinalIgnoreCase));

        if (schemaInstance is null)
        {
            var schemaType = ResolveSchemaTypeByName(schemaTypes, schemaName);
            if (schemaType is not null)
                schemaInstance = (IKeyspace)sp.GetRequiredService(schemaType);
        }

        return schemaInstance;
    }

    // Reuse your existing [Keyspace] attribute (same as Scylla) if you have schema classes.
    private static Type? ResolveSchemaTypeByName(Type[] schemaTypes, string schemaName) =>
        schemaTypes.FirstOrDefault(t =>
        {
            var attr = t.GetCustomAttribute<KeyspaceAttribute>();
            return attr != null && string.Equals(attr.Name, schemaName, StringComparison.OrdinalIgnoreCase);
        });

    private static async Task CreateTablesAndRunHooksAsync(
        ISqlDatabaseProvider provider,
        IServiceProvider sp,
        ILogger<PostgresDBConfiguration> logger,
        IKeyspace schemaInstance,
        Type[] modelTypes)
    {
        foreach (var modelType in modelTypes)
        {
            try
            {
                await provider.CreateTableAsync(modelType, schemaInstance);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, $"Failed to create table for {modelType.Name}. Reason: {ex.Message}");
                continue;
            }

            var instance = modelType.GetConstructor(Type.EmptyTypes)!.Invoke(null) as IVaultModel;

            try
            {
                if (instance is IBeforeVaultCreate before)
                    await before.BeforeCreateAsync(sp);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, $"Failed to run before actions for {modelType.Name}. Reason: {ex.Message}");
            }

            try
            {
                var preloadInterface = instance!
                    .GetType()
                    .GetInterfaces()
                    .FirstOrDefault(i =>
                        i.IsGenericType &&
                        i.GetGenericTypeDefinition() == typeof(IOnVaultCreate<>));

                if (preloadInterface is not null)
                {
                    var onCreateAsync = preloadInterface.GetMethod("OnCreateAsync")!;
                    var taskObj = (Task)onCreateAsync.Invoke(instance, new object[] { sp })!;
                    await taskObj.ConfigureAwait(false);

                    var resultProp = taskObj.GetType().GetProperty("Result")!;
                    var resultObj = resultProp.GetValue(taskObj);

                    if (resultObj is IEnumerable resultEnumerable)
                    {
                        int loadedCount = 0;
                        foreach (var _ in resultEnumerable)
                            loadedCount++;

                        if (loadedCount > 0)
                        {
                            var vaultIface = typeof(IVault<>).MakeGenericType(modelType);
                            var vault = sp.GetRequiredService(vaultIface);

                            var countMethod = vaultIface.GetMethod("CountAsync")!;
                            var countTask = (Task)countMethod.Invoke(vault, Array.Empty<object>())!;
                            await countTask.ConfigureAwait(false);
                            var count = (long)countTask.GetType().GetProperty("Result")!.GetValue(countTask)!;

                            if (count == 0)
                            {
                                var castMethod = typeof(Enumerable).GetMethod("Cast", BindingFlags.Public | BindingFlags.Static)!
                                    .MakeGenericMethod(modelType);
                                var toListMethod = typeof(Enumerable).GetMethod("ToList", BindingFlags.Public | BindingFlags.Static)!
                                    .MakeGenericMethod(modelType);
                                var typedList = toListMethod.Invoke(
                                    null,
                                    new object[] { castMethod.Invoke(null, new object[] { resultObj })! })!;

                                var saveBatch = vaultIface.GetMethod("SaveBatchAsync", new[]
                                    {
                                        typeof(IEnumerable<>).MakeGenericType(modelType),
                                        typeof(bool?)
                                    })
                                               ?? vaultIface.GetMethod("SaveBatchAsync", new[]
                                                   { typeof(IEnumerable<>).MakeGenericType(modelType) });

                                object? saveTaskObj;
                                if (saveBatch!.GetParameters().Length == 2)
                                    saveTaskObj = saveBatch.Invoke(vault, new object?[] { typedList, null });
                                else
                                    saveTaskObj = saveBatch.Invoke(vault, new object?[] { typedList });

                                await ((Task)saveTaskObj!).ConfigureAwait(false);
                                logger.LogInformation($"Streamed {loadedCount} items into {modelType.Name}.");
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                logger.LogError(ex, $"Failed to run preload actions for {modelType.Name}. Reason: {ex.Message}");
            }

            try
            {
                if (instance is IAfterVaultCreate after)
                    await after.AfterCreateAsync(sp);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, $"Failed to run after actions for {modelType.Name}. Reason: {ex.Message}");
            }
        }
    }

    // ----------------- discovery helpers -----------------

    private static Document GetDocumentForModel(Type modelType) => Document.From(modelType);

    private static Assembly[] DiscoverAssemblies() =>
        AppDomain.CurrentDomain.GetAssemblies()
            .Where(a => !a.IsDynamic && !string.IsNullOrWhiteSpace(a.FullName))
            .ToArray();

    private static IEnumerable<Type> FindSchemaTypes(Assembly[] assemblies) =>
        TypeDiscovery.FindTypesWithAttribute<KeyspaceAttribute>(assemblies)
            .Where(t =>
                t is not null &&
                t.IsClass &&
                !t.IsAbstract &&
                typeof(IKeyspace).IsAssignableFrom(t))!;

    private static IEnumerable<Type> FindVaultModelTypes(Assembly[] assemblies) =>
        TypeDiscovery.FindTypesWithAttribute<VaultAttribute>(assemblies)
            .Where(t =>
                t is not null &&
                t.IsClass &&
                !t.IsAbstract &&
                typeof(IVaultModel).IsAssignableFrom(t))!;

    private static string GetSchemaName(Type modelType)
    {
        // Reuse VaultAttribute.Keyspace (Cassandra "keyspace") as Postgres schema name
        var va = modelType.GetCustomAttribute<VaultAttribute>();
        return string.IsNullOrWhiteSpace(va?.Keyspace) ? "public" : va!.Keyspace!;
    }

    // -------------------------------
    // Npgsql + transactional wiring
    // -------------------------------

    private static void RegisterNpgsqlDataSource(IServiceCollection services, IConfiguration cfg)
    {
        // 1) Try classic connection string first (backwards compatibility).
        var connString = cfg.GetConnectionString("postgres");

        // 2) If not found, build from structured config:
        //    altruist:persistence:database:{provider,host,port,username,password,database}
        if (string.IsNullOrWhiteSpace(connString))
        {
            var dbSection = cfg.GetSection("altruist:persistence:database");

            var provider = dbSection["provider"];
            if (!string.Equals(provider, "postgres", StringComparison.OrdinalIgnoreCase))
            {
                // If the configured provider is not postgres, don't register a data source.
                // This configuration object should only be used when provider == postgres.
                throw new InvalidOperationException(
                    $"PostgresDBConfiguration used but provider is '{provider ?? "<null>"}'.");
            }

            var host = dbSection["host"] ?? "localhost";
            var portString = dbSection["port"] ?? "5432";
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

            if (!int.TryParse(portString, out var port))
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

        services.AddSingleton(sp =>
        {
            var builder = new NpgsqlDataSourceBuilder(connString);
            // configure mappings here if needed
            return builder.Build();
        });
    }

    /// <summary>
    /// Discover all [Transactional] methods, populate registry, and wrap matching services with TransactionalDecorator.
    /// </summary>
    private static void RegisterTransactionalServices(IServiceCollection services, Assembly[] assemblies)
    {
        // 1) Scan assemblies and populate registry (methods, attributes, declaring types)
        TransactionalRegistry.WarmUp(assemblies);

        // 2) Wrap DI registrations that have transactional methods on their implementation type.
        //    We only touch services that already have an ImplementationType (not factory/instance only).
        var descriptors = services.ToList();
        services.Clear();

        foreach (var descriptor in descriptors)
        {
            if (descriptor.ImplementationType is { } implType &&
                TransactionalRegistry.HasTransactionalMethods(implType))
            {
                services.Add(WrapServiceIfTransactional(descriptor, implType));
            }
            else
            {
                services.Add(descriptor);
            }
        }
    }

    private static ServiceDescriptor WrapServiceIfTransactional(ServiceDescriptor descriptor, Type implType)
    {
        // We will create a factory that:
        // - resolves implementation via ActivatorUtilities
        // - wraps it with TransactionalDecorator<T>
        var serviceType = descriptor.ServiceType;
        var lifetime = descriptor.Lifetime;

        // We support interface- or class-based registrations
        var decoratorFactory = BuildDecoratorFactory(serviceType, implType);

        return new ServiceDescriptor(serviceType, decoratorFactory, lifetime);
    }

    private static Func<IServiceProvider, object> BuildDecoratorFactory(Type serviceType, Type implType)
    {
        // For open generic services you’d need more logic; here we assume closed types.
        var useType = implType;

        return sp =>
        {
            // Create the original implementation instance
            var inner = ActivatorUtilities.CreateInstance(sp, useType);

            // If the serviceType is an interface implemented by implType, we want the proxy as that interface
            var proxyServiceType = serviceType.IsAssignableFrom(useType) ? serviceType : useType;

            var createMethod = typeof(DispatchProxy)
                .GetMethods(BindingFlags.Public | BindingFlags.Static)
                .Single(m => m.Name == nameof(DispatchProxy.Create) && m.GetGenericArguments().Length == 2)
                .MakeGenericMethod(proxyServiceType, typeof(TransactionalDecorator<>).MakeGenericType(proxyServiceType));

            var proxy = (object)createMethod.Invoke(null, null)!;

            // Set decorator fields
            var decorator = (dynamic)proxy; // TransactionalDecorator<proxyServiceType>
            decorator.Inner = inner;
            decorator.DataSource = sp.GetRequiredService<NpgsqlDataSource>();

            return proxy;
        };
    }
}

// This stays shared so both bootstrap and factories can use it.
public sealed class DefaultSchema : IKeyspace
{
    public DefaultSchema(string? name = null)
    {
        Name = string.IsNullOrWhiteSpace(name) ? "public" : name!;
    }

    public string Name { get; }

    // Required by IKeyspace
    public IDatabaseServiceToken DatabaseToken => PostgresDBToken.Instance;
}
