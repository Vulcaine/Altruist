using System.Reflection;

using Altruist.Contracts;
using Altruist.UORM;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using Npgsql;

namespace Altruist.Persistence.Postgres;

public sealed class PostgresDBToken : IDatabaseServiceToken
{
    public static PostgresDBToken Instance { get; } = new PostgresDBToken();
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

        // 1) Discover schemas + vault models (ONLY [Vault]-annotated IVaultModel types)
        var schemaTypes = FindSchemaTypes(assemblies).ToArray();
        var vaultModelTypes = FindModelTypes(assemblies).ToArray();      // note: FindModelTypes, not FindVaultModelTypes
        var initializerTypes = FindInitializers(assemblies).ToArray();

        // 2) Register schemas (idempotent)
        using (var tmp = services.BuildServiceProvider())
        {
            var logger = tmp.GetRequiredService<ILoggerFactory>()
                            .CreateLogger<PostgresVaultConfiguration>();
            RegisterSchemas(services, cfg, schemaTypes, logger);
        }

        RegisterVaultsViaServiceFactory(services, vaultModelTypes);
        RegisterNpgsqlDataSource(services, cfg);
        RegisterTransactionalServices(services, assemblies);

        await BootstrapModelsAsync(
            services,
            vaultModelTypes,
            initializerTypes,
            "Vaults");

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

    // ----------------- Npgsql + transactional -----------------

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

        services.AddSingleton(sp =>
        {
            var builder = new NpgsqlDataSourceBuilder(connString);
            return builder.Build();
        });
    }

    private static void RegisterTransactionalServices(IServiceCollection services, Assembly[] assemblies)
    {
        // 1) Scan assemblies and populate registry
        TransactionalRegistry.WarmUp(assemblies);

        // 2) Wrap DI registrations that have transactional methods
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
        var serviceType = descriptor.ServiceType;
        var lifetime = descriptor.Lifetime;

        var decoratorFactory = BuildDecoratorFactory(serviceType, implType);

        return new ServiceDescriptor(serviceType, decoratorFactory, lifetime);
    }

    private static Func<IServiceProvider, object> BuildDecoratorFactory(Type serviceType, Type implType)
    {
        var useType = implType;

        return sp =>
        {
            var inner = ActivatorUtilities.CreateInstance(sp, useType);

            var proxyServiceType = serviceType.IsAssignableFrom(useType) ? serviceType : useType;

            var createMethod = typeof(DispatchProxy)
                .GetMethods(BindingFlags.Public | BindingFlags.Static)
                .Single(m => m.Name == nameof(DispatchProxy.Create) && m.GetGenericArguments().Length == 2)
                .MakeGenericMethod(proxyServiceType, typeof(TransactionalDecorator<>).MakeGenericType(proxyServiceType));

            var proxy = createMethod.Invoke(null, null)!;

            var decorator = (dynamic)proxy;
            decorator.Inner = inner;
            decorator.DataSource = sp.GetRequiredService<NpgsqlDataSource>();

            return proxy;
        };
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
