/*
Copyright 2025 Aron Gere

Licensed under the Apache License, Version 2.0 (the "License");
*/

using Altruist.Persistence;

using Microsoft.Extensions.Logging;

namespace Altruist.Migrations.Postgres;

[Service(typeof(IVaultSchemaMigrator))]
[ConditionalOnConfig("altruist:persistence:database:provider", havingValue: "postgres")]
public sealed class PostgresVaultSchemaMigrator : IVaultSchemaMigrator
{
    private readonly ISchemaInspector _inspector;
    private readonly IMigrationPlanner _planner;
    private readonly IMigrationExecutor _executor;

    private readonly ILoggerFactory _logger;

    public PostgresVaultSchemaMigrator(
        ISchemaInspector inspector,
        IMigrationPlanner planner,
        IMigrationExecutor executor,
        ILoggerFactory loggerFactory
        )
    {
        _inspector = inspector;
        _planner = planner;
        _executor = executor;

        _logger = loggerFactory;
    }

    public async Task Migrate(IKeyspace schema, Type[] modelTypes, CancellationToken ct = default)
    {
        // 1) current model from DB
        var current = await _inspector.GetCurrentModelAsync(schema, ct).ConfigureAwait(false);

        // 2) desired model from vault models
        var desiredDocs = modelTypes.Select(e => Document.From(e)).ToArray();

        // 3) diff -> operations
        var operations = _planner.Plan(current, desiredDocs, schema.Name);

        // 4) execute
        await _executor.ApplyAsync(schema, operations).ConfigureAwait(false);
    }
}
