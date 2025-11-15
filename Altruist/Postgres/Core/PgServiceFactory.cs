/*
Copyright 2025 Aron Gere
Licensed under the Apache License, Version 2.0
*/

using System.Reflection;

using Altruist.Persistence;
using Altruist.UORM;

using Microsoft.Extensions.DependencyInjection;

namespace Altruist.Postgres
{
    /// <summary>
    /// Postgres-specific IServiceFactory implementation.
    ///
    /// Currently it knows how to create IVault&lt;T&gt; backed by PgVault&lt;T&gt;,
    /// but the contract is generic and can be extended later.
    /// </summary>
    [Service(typeof(IServiceFactory), ServiceLifetime.Singleton)]
    public sealed class PostgresServiceFactory : IServiceFactory
    {
        public bool CanCreate(Type serviceType)
        {
            // We only handle IVault<T> here.
            if (serviceType.GetGenericTypeDefinition() != typeof(IVault<>))
                return false;

            var modelType = serviceType.GetGenericArguments()[0];

            // Require IVaultModel + [Vault] on the model
            if (!typeof(IVaultModel).IsAssignableFrom(modelType))
                return false;

            var va = modelType.GetCustomAttribute<VaultAttribute>();
            return va != null;
        }

        public object Create(IServiceProvider sp, Type serviceType)
        {
            if (!CanCreate(serviceType))
                throw new InvalidOperationException($"PostgresServiceFactory cannot create service type {serviceType}.");

            var modelType = serviceType.GetGenericArguments()[0];

            var sqlProvider = sp.GetService<ISqlDatabaseProvider>();
            if (sqlProvider is null)
                throw new InvalidOperationException(
                    "ISqlDatabaseProvider is not registered. " +
                    "The Postgres provider package must register it before vaults are used.");

            var schemaName = GetSchemaName(modelType);

            // Try to resolve a registered IKeyspace with that name; if none, use a lightweight default
            var schema = sp.GetServices<IKeyspace>()
                           .FirstOrDefault(s => string.Equals(s.Name, schemaName, StringComparison.OrdinalIgnoreCase))
                        ?? new DefaultSchema(schemaName);

            var document = Document.From(modelType);
            var vaultType = typeof(PgVault<>).MakeGenericType(modelType);

            // PgVault<T>(ISqlDatabaseProvider provider, IKeyspace keyspace, Document document, IServiceProvider services)
            return Activator.CreateInstance(vaultType, sqlProvider, schema, document, sp)!;
        }

        private static string GetSchemaName(Type modelType)
        {
            var va = modelType.GetCustomAttribute<VaultAttribute>();
            return string.IsNullOrWhiteSpace(va?.Keyspace) ? "public" : va!.Keyspace!;
        }
    }
}
