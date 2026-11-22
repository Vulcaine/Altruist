/*
Copyright 2025 Aron Gere
Licensed under the Apache License, Version 2.0
*/

using System.Reflection;

using Altruist.Persistence;
using Altruist.Persistence.Postgres;
using Altruist.UORM;

using Microsoft.Extensions.DependencyInjection;

namespace Altruist.Testing.Internal;

/// <summary>
/// Per-test-class override of <see cref="IVault{T}"/> registrations. For every
/// <c>[Vault]</c>-marked model in loaded assemblies, registers a singleton factory
/// in <c>services</c> that builds a <see cref="PgVault{T}"/> against
/// the test schema regardless of <c>[Vault(Keyspace=…)]</c>. The new descriptors
/// shadow the root container's registrations because MEDI returns the last
/// matching descriptor for a closed generic <see cref="IVault{T}"/>.
///
/// <para>Avoids implementing <see cref="IServiceFactory"/> directly so Altruist's
/// type-discovery scan doesn't pick this up at root bootstrap and try to construct
/// it without the schema name.</para>
/// </summary>
internal static class TestVaultRegistration
{
    public static void RegisterTestVaults(IServiceCollection services, string testSchema)
    {
        var assemblies = AppDomain.CurrentDomain.GetAssemblies()
            .Where(a => !a.IsDynamic && !string.IsNullOrWhiteSpace(a.FullName));

        foreach (var asm in assemblies)
        {
            Type[] types;
            try { types = asm.GetTypes(); }
            catch (ReflectionTypeLoadException ex) { types = ex.Types.Where(t => t is not null).ToArray()!; }

            foreach (var modelType in types)
            {
                if (modelType is null || !modelType.IsClass || modelType.IsAbstract) continue;
                if (!typeof(IVaultModel).IsAssignableFrom(modelType)) continue;
                if (modelType.GetCustomAttribute<VaultAttribute>(inherit: true) is null) continue;

                var ivaultType = typeof(IVault<>).MakeGenericType(modelType);
                var capturedModel = modelType;
                services.AddSingleton(ivaultType, sp => BuildPgVault(sp, capturedModel, testSchema));
            }
        }
    }

    private static object BuildPgVault(IServiceProvider sp, Type modelType, string testSchema)
    {
        var va = modelType.GetCustomAttribute<VaultAttribute>()!;

        var sqlProvider = PostgresServiceFactory.ResolveProvider(sp, modelType, va.DbInstance);

        IKeyspace keyspace = new DefaultSchema(testSchema);
        var doc = VaultDocument.From(modelType);
        var vaultType = typeof(PgVault<>).MakeGenericType(modelType);
        return Activator.CreateInstance(vaultType, sqlProvider, keyspace, doc)!;
    }
}
