
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
        Task Migrate(Type[] modelTypes, CancellationToken cancellationToken = default);
    }

    [Service(typeof(IVaultSchemaMigrator))]
    public sealed class VaultSchemaMigrator : IVaultSchemaMigrator
    {
        private readonly ISchemaInspector _inspector;
        private readonly IMigrationPlanner _planner;
        private readonly IMigrationExecutor _executor;
        private readonly IEnumerable<IKeyspace> _keyspaces;
        private readonly ILogger<VaultSchemaMigrator> _logger;

        public VaultSchemaMigrator(
            ISchemaInspector inspector,
            IMigrationPlanner planner,
            IMigrationExecutor executor,
            IEnumerable<IKeyspace> keyspaces,
            ILoggerFactory loggerFactory)
        {
            _inspector = inspector ?? throw new ArgumentNullException(nameof(inspector));
            _planner = planner ?? throw new ArgumentNullException(nameof(planner));
            _executor = executor ?? throw new ArgumentNullException(nameof(executor));
            _keyspaces = keyspaces ?? throw new ArgumentNullException(nameof(keyspaces));
            _logger = loggerFactory.CreateLogger<VaultSchemaMigrator>();
        }

        public async Task Migrate(Type[] modelTypes, CancellationToken ct = default)
        {
            if (modelTypes is null || modelTypes.Length == 0)
                return;

            // 1) desired model from vault models
            var allDocs = modelTypes
                .Select(Document.From)
                .ToArray();

            // 2) global dependency-aware ordering across ALL keyspaces
            var orderedDocs = OrderDocumentsByDependencies(allDocs);

            // 3) group docs by schema/keyspace (normalized)
            var docsBySchema = orderedDocs
                .GroupBy(d => NormalizeSchemaName(d.Header.Keyspace))
                .ToList();

            // 4) per-schema migration, but always using the full ordered doc set for planning,
            //    so cross-schema FK resolution still sees every document.
            foreach (var group in docsBySchema)
            {
                var schemaName = group.Key;


                _logger.LogInformation("🔧 Migrating schema '{Schema}'...", schemaName);

                // 4.1) current model from DB (for this schema only)
                var current = await _inspector
                    .GetCurrentModelAsync(schemaName, ct)
                    .ConfigureAwait(false);

                // 4.2) diff -> operations (planner filters docs for this schema internally)
                var operations = _planner.Plan(current, orderedDocs, schemaName);

                if (operations.Count == 0)
                {
                    _logger.LogInformation("ℹ️ No migration operations for schema '{Schema}'.", schemaName);
                    continue;
                }

                // 4.3) execute operations for this schema
                await _executor.ApplyAsync(schemaName, operations).ConfigureAwait(false);

                _logger.LogInformation(
                    "✅ Schema '{Schema}' migration complete. {Count} operation(s) applied.",
                    schemaName,
                    operations.Count);
            }
        }

        // ----------------- helpers -----------------

        private IKeyspace? ResolveSchema(string schemaName)
        {
            // Try to resolve a user-defined IKeyspace, fall back to DefaultSchema.
            var ks = _keyspaces.FirstOrDefault(s =>
                string.Equals(s.Name, schemaName, StringComparison.OrdinalIgnoreCase));

            return ks;
        }

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
        private static IReadOnlyList<Document> OrderDocumentsByDependencies(IReadOnlyList<Document> docs)
        {
            if (docs.Count <= 1)
                return docs;

            // Type -> Document lookup
            var docsByType = docs
                .GroupBy(d => d.Type)
                .ToDictionary(g => g.Key, g => g.First());

            // Graph: principalDoc -> dependents
            var adjacency = new Dictionary<Document, HashSet<Document>>();
            var inDegree = new Dictionary<Document, int>();

            foreach (var doc in docs)
            {
                adjacency[doc] = new HashSet<Document>();
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
                        // The planner will still validate existence / correctness.
                        continue;
                    }

                    // Self-FK doesn't affect which table must be created first; ignore in ordering.
                    if (ReferenceEquals(principalDoc, doc))
                        continue;

                    if (!adjacency.TryGetValue(principalDoc, out var deps))
                    {
                        deps = new HashSet<Document>();
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
            var queue = new Queue<Document>(inDegree.Where(kv => kv.Value == 0).Select(kv => kv.Key));
            var sorted = new List<Document>(docs.Count);

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
