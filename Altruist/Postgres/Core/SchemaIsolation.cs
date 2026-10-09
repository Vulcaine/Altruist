/*
Copyright 2025 Aron Gere
Licensed under the Apache License, Version 2.0
*/

using System.Reflection;

using Altruist.UORM;

using Npgsql;

namespace Altruist.Persistence.Postgres;

/// <summary>
/// Postgres-specific schema lifecycle helpers. Operates via raw Npgsql so callers
/// don't have to invoke Altruist's full migration pipeline (which is keyed by
/// <c>[Vault].Keyspace</c> at attribute level and would migrate into the
/// production-named schemas, not the isolated copies these helpers create).
///
/// <para><b>Strategy.</b> The root bootstrap creates the production-named schemas
/// (e.g. <c>player</c>, <c>account</c>) in whatever database <c>config.yml</c>
/// points at — these become structural templates. Callers ask for a fresh isolated
/// schema (test_X, e2e_X, etc.) and this helper does
/// <c>CREATE TABLE isolated.tbl (LIKE prod.tbl INCLUDING ALL)</c> for every
/// <c>[Vault]</c>-marked model, which copies columns, defaults, indexes, and
/// constraints but no data.</para>
///
/// <para>Two consumers:
/// <list type="bullet">
/// <item>The server-side <c>[AltruistTest]</c> per-class runner — clones into
/// <c>test_&lt;classname&gt;</c> schemas at the start of each test class.</item>
/// <item>The E2E session controller (gated by <c>altruist:e2e:enabled</c>) —
/// resets <c>e2e_&lt;keyspace&gt;</c> schemas on demand from out-of-process
/// tests.</item>
/// </list>
/// </para>
///
/// <para><b>Test infrastructure only.</b> Every call drops/creates schemas with DDL on its own connection; never
/// point it at a schema that holds real data. Table discovery scans all loaded assemblies for non-abstract classes
/// carrying <see cref="VaultAttribute"/> (schema = its <c>Keyspace</c>, or <c>public</c> when empty; table = its
/// <c>Name</c>). All tables land in the one target schema, so two vaults with the same table name in different
/// keyspaces collide, and <c>&lt;table&gt;_history</c> tables are not cloned. Not to be confused with the
/// E2E reset endpoint (<see cref="E2E.E2ESessionController"/>), which truncates the real tables instead.</para>
/// </summary>
public static class SchemaIsolation
{
    /// <summary>
    /// Compute a per-test-class schema name. Lowercased, alphanumeric +
    /// underscores only, prefixed with <c>test_</c> to avoid colliding with prod names.
    /// </summary>
    /// <param name="testClass">The test class; only its simple <see cref="System.Reflection.MemberInfo.Name"/> is used,
    /// so same-named classes in different namespaces share a schema.</param>
    /// <returns>e.g. <c>test_myfixture</c> (any char other than a letter, digit or underscore becomes <c>_</c>).</returns>
    public static string SchemaNameFor(Type testClass)
    {
        var raw = testClass.Name.ToLowerInvariant();
        var safe = new string(raw.Select(c => char.IsLetterOrDigit(c) || c == '_' ? c : '_').ToArray());
        return $"test_{safe}";
    }

    /// <summary>
    /// Drop the schema if it exists, then create it fresh, then clone every
    /// discovered <c>[Vault]</c> table from its production schema into the target
    /// schema using <c>CREATE TABLE schema.table (LIKE source.table INCLUDING ALL)</c>.
    /// </summary>
    /// <remarks>
    /// Destructive for <paramref name="targetSchema"/> (<c>DROP SCHEMA ... CASCADE</c>). The source tables must already
    /// exist (i.e. normal startup migration has run); a missing source table throws a <see cref="PostgresException"/>.
    /// Foreign keys are not copied by <c>LIKE ... INCLUDING ALL</c>.
    /// </remarks>
    /// <param name="dataSource">Data source used to open one connection for all statements.</param>
    /// <param name="targetSchema">Schema to recreate (quoted as-is; pass a trusted name such as <see cref="SchemaNameFor"/>).</param>
    public static async Task EnsureFreshSchemaAsync(NpgsqlDataSource dataSource, string targetSchema)
    {
        var vaults = DiscoverVaultTables();

        await using var conn = await dataSource.OpenConnectionAsync();

        await Execute(conn, $"DROP SCHEMA IF EXISTS \"{targetSchema}\" CASCADE");
        await Execute(conn, $"CREATE SCHEMA \"{targetSchema}\"");

        foreach (var (sourceSchema, table) in vaults)
        {
            var sql = $"CREATE TABLE \"{targetSchema}\".\"{table}\" " +
                      $"(LIKE \"{sourceSchema}\".\"{table}\" INCLUDING ALL)";
            await Execute(conn, sql);
        }
    }

    /// <summary>Drop the target schema and everything in it (<c>DROP SCHEMA IF EXISTS ... CASCADE</c>).</summary>
    /// <param name="dataSource">Data source used to open the connection.</param>
    /// <param name="targetSchema">Schema to drop.</param>
    public static async Task DropSchemaAsync(NpgsqlDataSource dataSource, string targetSchema)
    {
        await using var conn = await dataSource.OpenConnectionAsync();
        await Execute(conn, $"DROP SCHEMA IF EXISTS \"{targetSchema}\" CASCADE");
    }

    /// <summary>
    /// Find every loaded <c>[Vault]</c>-marked vault model type and return its
    /// (keyspace, table) tuple. Uses <see cref="VaultAttribute"/> on the model class.
    /// </summary>
    private static IEnumerable<(string Schema, string Table)> DiscoverVaultTables()
    {
        var assemblies = AppDomain.CurrentDomain.GetAssemblies()
            .Where(a => !a.IsDynamic && !string.IsNullOrWhiteSpace(a.FullName));

        foreach (var asm in assemblies)
        {
            Type[] types;
            try { types = asm.GetTypes(); }
            catch (ReflectionTypeLoadException ex) { types = ex.Types.Where(t => t is not null).ToArray()!; }

            foreach (var t in types)
            {
                if (t is null || !t.IsClass || t.IsAbstract) continue;
                var attr = t.GetCustomAttribute<VaultAttribute>(inherit: true);
                if (attr is null) continue;
                var schema = string.IsNullOrWhiteSpace(attr.Keyspace) ? "public" : attr.Keyspace!.Trim();
                var table = string.IsNullOrWhiteSpace(attr.Name) ? t.Name : attr.Name!.Trim();
                yield return (schema, table);
            }
        }
    }

    private static async Task Execute(NpgsqlConnection conn, string sql)
    {
        await using var cmd = new NpgsqlCommand(sql, conn);
        await cmd.ExecuteNonQueryAsync();
    }
}
