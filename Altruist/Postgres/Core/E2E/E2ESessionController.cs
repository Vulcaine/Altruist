/*
Copyright 2025 Aron Gere
Licensed under the Apache License, Version 2.0
*/

using System.Reflection;

using Altruist.UORM;

using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

using Npgsql;

namespace Altruist.Persistence.Postgres.E2E;

/// <summary>
/// Test-only HTTP surface for client E2E test fixtures. Lets a client-side test
/// harness reset persistent state (so tests don't leak rows across runs) without
/// needing direct database access.
///
/// <para><b>Gated by <c>altruist:e2e:enabled</c>.</b> The
/// <see cref="ConditionalOnConfigAttribute"/> below means this controller is only
/// registered when the server is started with that flag set in <c>config.yml</c>
/// (or its overlays). On any production server, the route never exists — a
/// <c>POST /e2e/v1/reset</c> against a real deployment returns 404, not 200.</para>
///
/// <para><b>Deployment.</b> Intended for use with a dedicated test stack
/// (<c>docker-compose-e2e.yml</c>) that points the server at a separate Postgres
/// database. Do <em>not</em> enable this flag on a server that shares its database
/// with production traffic.</para>
///
/// <para>Requires a registered <see cref="NpgsqlDataSource"/> (Postgres configuration registers one). For per-test-class
/// isolated schemas inside the server process use <see cref="SchemaIsolation"/> instead.</para>
/// </summary>
[ApiController]
[Route("/e2e/v1")]
[ConditionalOnConfig("altruist:e2e:enabled", havingValue: "true")]
public sealed class E2ESessionController : ControllerBase
{
    private readonly NpgsqlDataSource _dataSource;
    private readonly ILogger<E2ESessionController> _logger;

    /// <summary>Creates the controller.</summary>
    /// <param name="dataSource">Data source the reset runs on.</param>
    /// <param name="logger">Logger.</param>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    public E2ESessionController(NpgsqlDataSource dataSource, ILogger<E2ESessionController> logger)
    {
        _dataSource = dataSource ?? throw new ArgumentNullException(nameof(dataSource));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Truncate every <c>[Vault]</c>-marked table across every keyspace. Schema
    /// structure is preserved (no DROP/CREATE), so the migration step that runs
    /// at server boot doesn't need to re-execute. Foreign keys are honored via
    /// <c>RESTART IDENTITY CASCADE</c>.
    /// </summary>
    /// <remarks>
    /// <c>POST /e2e/v1/reset</c>. Tables are discovered from loaded assemblies (non-abstract classes with
    /// <see cref="VaultAttribute"/>, deduplicated). All tables are truncated in one statement, so a discovered table
    /// that does not exist in the database fails the whole request. <c>&lt;table&gt;_history</c> tables are not truncated.
    /// </remarks>
    /// <param name="ct">Request cancellation.</param>
    /// <returns><c>200 OK</c> with <c>{ "truncated": &lt;table count&gt; }</c>.</returns>
    [HttpPost("reset")]
    public async Task<IActionResult> Reset(CancellationToken ct)
    {
        var tables = DiscoverVaultTables().ToList();
        if (tables.Count == 0)
        {
            _logger.LogWarning("[E2E] /reset called but no [Vault]-marked tables were discovered.");
            return Ok(new { truncated = 0 });
        }

        await using var conn = await _dataSource.OpenConnectionAsync(ct);

        // Single statement: TRUNCATE TABLE "k1"."t1", "k2"."t2", ... RESTART IDENTITY CASCADE
        // CASCADE handles FK chains; RESTART IDENTITY resets sequences so seeded IDs are predictable.
        var qualified = string.Join(", ", tables.Select(t => $"\"{t.Schema}\".\"{t.Table}\""));
        var sql = $"TRUNCATE TABLE {qualified} RESTART IDENTITY CASCADE";

        await using var cmd = new NpgsqlCommand(sql, conn);
        await cmd.ExecuteNonQueryAsync(ct);

        _logger.LogInformation("[E2E] /reset truncated {Count} vault table(s).", tables.Count);
        return Ok(new { truncated = tables.Count });
    }

    private static IEnumerable<(string Schema, string Table)> DiscoverVaultTables()
    {
        var seen = new HashSet<(string, string)>();
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            if (asm.IsDynamic || string.IsNullOrWhiteSpace(asm.FullName)) continue;

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
                if (seen.Add((schema, table)))
                    yield return (schema, table);
            }
        }
    }
}
