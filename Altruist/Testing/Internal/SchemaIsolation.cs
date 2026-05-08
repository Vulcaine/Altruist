/*
Copyright 2025 Aron Gere
Licensed under the Apache License, Version 2.0
*/

using System.Reflection;

using Altruist.UORM;

using Npgsql;

namespace Altruist.Testing.Internal;

/// <summary>
/// Postgres-specific schema lifecycle helpers used by the per-test-class runner.
/// Operates via raw Npgsql so it doesn't have to invoke Altruist's full migration
/// pipeline (which is keyed by <c>[Vault].Keyspace</c> at attribute level and would
/// migrate into the production-named schemas, not our test ones).
///
/// <para><b>Strategy.</b> The root bootstrap creates the production-named schemas
/// (e.g. <c>player</c>, <c>account</c>) in whatever database <c>config.yml</c>
/// points at — these become structural templates. Per test class we
/// <c>CREATE TABLE test_X.tbl (LIKE prod.tbl INCLUDING ALL)</c>, which copies
/// columns, defaults, indexes, and constraints. Vault queries then run against
/// <c>test_X</c> via <see cref="TestPostgresServiceFactory"/>.</para>
/// </summary>
internal static class SchemaIsolation
{
    /// <summary>
    /// Compute the test schema name from a test class type. Lowercased, alphanumeric +
    /// underscores only, prefixed with <c>test_</c> to avoid colliding with prod names.
    /// </summary>
    public static string SchemaNameFor(Type testClass)
    {
        var raw = testClass.Name.ToLowerInvariant();
        var safe = new string(raw.Select(c => char.IsLetterOrDigit(c) || c == '_' ? c : '_').ToArray());
        return $"test_{safe}";
    }

    /// <summary>
    /// Drop the schema if it exists, then create it fresh, then clone every
    /// discovered <c>[Vault]</c> table from its production schema into the test
    /// schema using <c>CREATE TABLE schema.table (LIKE source.table INCLUDING ALL)</c>.
    /// </summary>
    public static async Task EnsureFreshSchemaAsync(NpgsqlDataSource dataSource, string testSchema)
    {
        var vaults = DiscoverVaultTables();

        await using var conn = await dataSource.OpenConnectionAsync();

        // Purge any leftover from a previous run.
        await Execute(conn, $"DROP SCHEMA IF EXISTS \"{testSchema}\" CASCADE");
        await Execute(conn, $"CREATE SCHEMA \"{testSchema}\"");

        // Clone every vault table structurally. INCLUDING ALL pulls columns,
        // defaults, indexes, constraints, etc. — but does NOT copy data.
        foreach (var (sourceSchema, table) in vaults)
        {
            var sql = $"CREATE TABLE \"{testSchema}\".\"{table}\" " +
                      $"(LIKE \"{sourceSchema}\".\"{table}\" INCLUDING ALL)";
            await Execute(conn, sql);
        }
    }

    /// <summary>Drop the test schema and everything in it.</summary>
    public static async Task DropSchemaAsync(NpgsqlDataSource dataSource, string testSchema)
    {
        await using var conn = await dataSource.OpenConnectionAsync();
        await Execute(conn, $"DROP SCHEMA IF EXISTS \"{testSchema}\" CASCADE");
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
