
/*
Copyright 2025 Aron Gere

Licensed under the Apache License, Version 2.0 (the "License");
you may not use this file except in compliance with the License.
You may obtain a copy of the License at

    http://www.apache.org/licenses/LICENSE-2.0
*/

using Altruist.Persistence;

using Microsoft.Extensions.Logging;

namespace Altruist.Migrations
{
    /// <summary>
    /// Provider-agnostic contract for applying vault schema to a physical database.
    /// Implementations are provider-specific (Postgres, Scylla, etc.).
    /// </summary>
    public interface IVaultSchemaMigrator
    {
        /// <summary>
        /// Ensure that all tables / indexes / constraints for the given vault models
        /// exist in the given logical schema (keyspace).
        /// </summary>
        Task Migrate(IKeyspace schema, Type[] modelTypes, CancellationToken cancellationToken = default);
    }


    [Service(typeof(IVaultSchemaMigrator))]
    public sealed class VaultSchemaMigrator : IVaultSchemaMigrator
    {
        private readonly ISchemaInspector _inspector;
        private readonly IMigrationPlanner _planner;
        private readonly IMigrationExecutor _executor;

        private readonly ILoggerFactory _logger;

        public VaultSchemaMigrator(
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

}
