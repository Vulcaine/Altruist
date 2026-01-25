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
        /// exist in the database, across all logical schemas (keyspaces).
        /// </summary>
        Task Migrate(Type[] modelTypes, CancellationToken cancellationToken = default);
    }

    [Service(typeof(IVaultSchemaMigrator))]
    [ConditionalOnConfig("altruist:persistence:database")]
    public sealed class VaultSchemaMigrator : IVaultSchemaMigrator
    {
        private readonly ISchemaInspector _inspector;
        private readonly IMigrationPlanner _planner;
        private readonly IMigrationExecutor _executor;
        private readonly ILogger<VaultSchemaMigrator> _logger;

        public VaultSchemaMigrator(
            ISchemaInspector inspector,
            IMigrationPlanner planner,
            IMigrationExecutor executor,
            ILoggerFactory loggerFactory)
        {
            _inspector = inspector ?? throw new ArgumentNullException(nameof(inspector));
            _planner = planner ?? throw new ArgumentNullException(nameof(planner));
            _executor = executor ?? throw new ArgumentNullException(nameof(executor));
            _logger = loggerFactory.CreateLogger<VaultSchemaMigrator>();
        }

        public async Task Migrate(Type[] modelTypes, CancellationToken ct = default)
        {
            if (modelTypes is null || modelTypes.Length == 0)
                return;

            // 1) desired model from vault models
            var initialDocs = modelTypes
                .Select(VaultDocument.From)
                .ToArray();

            // FIX: expand docs with FK principal docs so planner can resolve principal schema/table/column
            var allDocs = ExpandWithForeignKeyPrincipals(initialDocs);

            // 2) global dependency-aware ordering across ALL keyspaces
            var orderedDocs = OrderDocumentsByDependencies(allDocs);

            // 3) determine which schemas exist (from the ordered docs)
            var schemaNames = orderedDocs
                .Select(d => NormalizeSchemaName(d.Header.Keyspace))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            // 4) load current database model per schema
            var currentBySchema = new Dictionary<string, DatabaseModel>(StringComparer.OrdinalIgnoreCase);

            foreach (var schemaName in schemaNames)
            {
                _logger.LogInformation("🔍 Inspecting schema '{Schema}'...", schemaName);

                var current = await _inspector
                    .GetCurrentModelAsync(schemaName, ct)
                    .ConfigureAwait(false);

                currentBySchema[schemaName] = current;
            }

            // 5) diff -> operations
            var operations = _planner.Plan(currentBySchema, orderedDocs);

            if (operations.Count == 0)
            {
                _logger.LogInformation("ℹ️ No migration operations to apply.");
                return;
            }

            // 6) execute all operations
            var defaultSchema = "altruist";

            await _executor.ApplyAsync(defaultSchema, operations).ConfigureAwait(false);

            _logger.LogInformation(
                "✅ Vault schema migration complete. {Count} operation(s) applied.",
                operations.Count);
        }

        private static VaultDocument[] ExpandWithForeignKeyPrincipals(IReadOnlyList<VaultDocument> docs)
        {
            var byType = new Dictionary<Type, VaultDocument>();
            var queue = new Queue<VaultDocument>();

            foreach (var d in docs)
            {
                if (!byType.ContainsKey(d.Type))
                    byType[d.Type] = d;
                queue.Enqueue(d);
            }

            while (queue.Count > 0)
            {
                var doc = queue.Dequeue();

                if (doc.ForeignKeys is null || doc.ForeignKeys.Count == 0)
                    continue;

                foreach (var fk in doc.ForeignKeys)
                {
                    var principalType = fk.PrincipalType;
                    if (byType.ContainsKey(principalType))
                        continue;

                    // Will throw if principal type lacks [Vault]/[Prefab] – that’s good feedback.
                    var principalDoc = VaultDocument.From(principalType);
                    byType[principalType] = principalDoc;
                    queue.Enqueue(principalDoc);
                }
            }

            return byType.Values.ToArray();
        }

        // ----------------- helpers -----------------

        private static string NormalizeSchemaName(string? keyspace)
        {
            var s = keyspace;
            if (string.IsNullOrWhiteSpace(s))
                s = "public";

            return s.Trim().ToLowerInvariant();
        }

        /// <summary>
        /// Topologically sorts documents so that any principal document appears
        /// before dependents that reference it via foreign keys.
        ///
        /// - Works across all keyspaces (Keyspace is not used here).
        /// - Ignores FKs whose principal type isn't in the given list (planner will
        ///   still validate those separately).
        /// - Ignores self-FKs for ordering (they don't affect table creation order).
        /// - Throws if there is a circular dependency between different documents.
        /// </summary>
        private static IReadOnlyList<VaultDocument> OrderDocumentsByDependencies(IReadOnlyList<VaultDocument> docs)
        {
            if (docs.Count <= 1)
                return docs;

            // Type -> Document lookup
            var docsByType = docs
                .GroupBy(d => d.Type)
                .ToDictionary(g => g.Key, g => g.First());

            // Graph: principalDoc -> dependents
            var adjacency = new Dictionary<VaultDocument, HashSet<VaultDocument>>();
            var inDegree = new Dictionary<VaultDocument, int>();

            foreach (var doc in docs)
            {
                adjacency[doc] = new HashSet<VaultDocument>();
                inDegree[doc] = 0;
            }

            // Build the dependency graph
            foreach (var doc in docs)
            {
                foreach (var fk in doc.ForeignKeys)
                {
                    if (!docsByType.TryGetValue(fk.PrincipalType, out var principalDoc))
                    {
                        // Principal type not in this migration batch; ignore for ordering.
                        continue;
                    }

                    // Self-FK doesn't affect which table must be created first; ignore in ordering.
                    if (ReferenceEquals(principalDoc, doc))
                        continue;

                    if (!adjacency.TryGetValue(principalDoc, out var deps))
                    {
                        deps = new HashSet<VaultDocument>();
                        adjacency[principalDoc] = deps;
                    }

                    // Edge: principalDoc -> doc
                    if (deps.Add(doc))
                    {
                        inDegree[doc] = inDegree[doc] + 1;
                    }
                }
            }

            // Kahn's algorithm for topological sort
            var queue = new Queue<VaultDocument>(inDegree.Where(kv => kv.Value == 0).Select(kv => kv.Key));
            var sorted = new List<VaultDocument>(docs.Count);

            while (queue.Count > 0)
            {
                var node = queue.Dequeue();
                sorted.Add(node);

                if (!adjacency.TryGetValue(node, out var dependents))
                    continue;

                foreach (var dependent in dependents)
                {
                    inDegree[dependent] = inDegree[dependent] - 1;
                    if (inDegree[dependent] == 0)
                        queue.Enqueue(dependent);
                }
            }

            if (sorted.Count != docs.Count)
            {
                // Some nodes still have in-degree > 0 => cycle
                var cyclicDocs = inDegree
                    .Where(kv => kv.Value > 0)
                    .Select(kv => kv.Key.Type.Name)
                    .Distinct()
                    .OrderBy(n => n)
                    .ToArray();

                throw new InvalidOperationException(
                    "Detected circular foreign-key dependency among vaults: " +
                    string.Join(", ", cyclicDocs));
            }

            return sorted;
        }
    }
}
