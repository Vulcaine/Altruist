// PgPrefabs.cs (updates only)

using System.Collections;
using System.ComponentModel;
using System.Reflection;

using Altruist.UORM;

namespace Altruist.Persistence;

/// <summary>
/// Postgres implementation of <see cref="IPrefabs"/>: queries prefab aggregates (a root vault row plus related
/// component rows from other vault tables) and saves their components back with plain upserts.
/// </summary>
/// <remarks>
/// <para>
/// Registered as a singleton <see cref="IPrefabs"/> when <c>altruist:persistence:database:provider</c> is
/// <c>postgres</c>. Choose prefabs when you want an aggregate loaded as objects in a few round trips (one query for
/// the roots, one per included component). For one table use <c>IVault&lt;T&gt;</c>; for flat multi-table rows or
/// projections use the join querying layer (<see cref="Postgres.PgVaultQuery"/>).
/// </para>
/// <para>
/// Queries and saves always run on the unkeyed <see cref="ISqlDatabaseProvider"/>, regardless of any
/// <c>DbInstance</c> on the component vaults. Unlike <c>IVault&lt;T&gt;.SaveAsync</c>, prefab saves do <b>not</b>
/// call <see cref="IVaultModel.OnSave"/> and do <b>not</b> do version-checked optimistic concurrency: each dirty model
/// is upserted on its <c>StorageId</c> column and every other column (including <c>version</c>) is overwritten with
/// the in-memory value. A model whose table also has a <c>[VaultUniqueKey]</c> may therefore fail with a unique
/// violation instead of updating. All upserts go to the database as one multi-statement command.
/// </para>
/// </remarks>
/// <example>
/// <code>
/// // load roots matching a filter, with two components hydrated
/// var list = await prefabs.Query&lt;PlayerPrefab&gt;()
///     .Where(p =&gt; p.Account.Region == region &amp;&amp; p.Items.Any(i =&gt; i.Kind == "rare"))
///     .Include(p =&gt; p.Items)
///     .Include(p =&gt; p.Guild)
///     .ToListAsync(ct);
///
/// var player = list[0];
/// player.Items.Add(new ItemVault { StorageId = Guid.NewGuid().ToString(), AccountId = player.Account.StorageId });
/// await prefabs.SaveComponentAsync(player, nameof(PlayerPrefab.Items), ct); // upserts the Items rows only
/// </code>
/// </example>
[Service(typeof(IPrefabs))]
[ConditionalOnConfig("altruist:persistence:database:provider", havingValue: "postgres")]
public sealed class PgPrefabs : IPrefabs
{
    private readonly ISqlDatabaseProvider _db;

    /// <summary>Creates the service.</summary>
    /// <param name="db">Provider used for all prefab queries and saves.</param>
    public PgPrefabs(ISqlDatabaseProvider db) => _db = db;

    /// <inheritdoc/>
    /// <remarks>
    /// The returned builder is mutable and single-use. Filter support (translated to bound parameters):
    /// <c>&amp;&amp;</c>, <c>||</c>, and <c>==</c> / <c>!=</c> between <c>p.Component.Member</c> and a value, where the
    /// component is the root (direct column test) or a single reference (<c>EXISTS</c> sub-query); on collection
    /// components only <c>p.Items.Any()</c> or <c>p.Items.Any(i =&gt; ...)</c> with the same <c>==</c>/<c>!=</c>,
    /// <c>&amp;&amp;</c>, <c>||</c> rules inside. Comparing to <c>null</c> becomes <c>IS [NOT] NULL</c>. Not supported
    /// (throws <see cref="NotSupportedException"/>): <c>!</c>, <c>&lt;</c>/<c>&gt;</c>-style operators, bare bool members,
    /// other method calls, nested <c>Any</c>, and (inside <c>Any</c>) members wrapped in a conversion such as enum or
    /// nullable comparisons. An unknown <c>Any</c> component throws <see cref="InvalidOperationException"/>.
    /// Results have no defined order; <c>FirstOrDefaultAsync</c> returns an arbitrary match. <c>Include</c> of a name
    /// that is not a component is ignored.
    /// </remarks>
    public IPrefabQuery<TPrefab> Query<TPrefab>()
        where TPrefab : PrefabModel, new()
        => new PgPrefabQuery<TPrefab>(_db);

    /// <inheritdoc/>
    /// <remarks>
    /// The root must be non-null even when saving another component. A null component is a no-op. Passing the root
    /// property name saves only the root.
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="prefab"/> is null.</exception>
    /// <exception cref="InvalidOperationException">The root is null, <paramref name="componentName"/> is unknown, or a model has an empty StorageId.</exception>
    public Task SaveComponentAsync(PrefabModel prefab, string componentName, CancellationToken ct = default)
        => SaveInternalAsync(prefab, componentName, ct);

    /// <inheritdoc/>
    /// <remarks>
    /// Models are de-duplicated by type and StorageId. After a successful write, a public parameterless
    /// <c>AcceptChanges()</c> on each written model is invoked when present.
    /// </remarks>
    public Task SaveAsync(PrefabModel prefab, CancellationToken ct = default)
        => SaveInternalAsync(prefab, componentName: null, ct);

    private async Task SaveInternalAsync(PrefabModel prefab, string? componentName, CancellationToken ct)
    {
        if (prefab is null)
            throw new ArgumentNullException(nameof(prefab));

        ct.ThrowIfCancellationRequested();

        var meta = PrefabDocument.Get(prefab.GetType());
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var candidates = new List<IVaultModel>(capacity: 32);

        void Add(IVaultModel m)
        {
            if (m is null)
                return;

            if (string.IsNullOrWhiteSpace(m.StorageId))
                throw new InvalidOperationException($"Cannot save {m.GetType().Name} with empty StorageId.");

            var key = $"{m.GetType().FullName}:{m.StorageId}";
            if (seen.Add(key))
                candidates.Add(m);
        }

        // Root is always validated (must exist), but only saved when:
        // - saving all components (componentName == null), OR
        // - caller explicitly requests the root component
        var root = meta.ComponentsByName[meta.RootPropertyName].Property.GetValue(prefab) as IVaultModel
            ?? throw new InvalidOperationException(
                $"Prefab root '{meta.RootPropertyName}' is null (or not IVaultModel) on {prefab.GetType().Name}.");

        if (componentName is null ||
            string.Equals(componentName, meta.RootPropertyName, StringComparison.Ordinal))
        {
            Add(root);
        }

        if (componentName is null)
        {
            // Save ALL hydrated components currently on the prefab
            foreach (var comp in meta.ComponentsByName.Values)
            {
                if (comp.Kind == PrefabComponentKind.Root)
                    continue;

                var value = comp.Property.GetValue(prefab);
                if (value is null)
                    continue;

                AddComponentValue(value);
            }
        }
        else
        {
            if (!meta.ComponentsByName.TryGetValue(componentName, out var comp))
                throw new InvalidOperationException(
                    $"Prefab component '{componentName}' not found on {prefab.GetType().Name}.");

            if (comp.Kind != PrefabComponentKind.Root)
            {
                var value = comp.Property.GetValue(prefab);
                if (value is null)
                    return;

                AddComponentValue(value);
            }
        }

        if (candidates.Count == 0)
            return;

        // Best-effort dirty detection; if unknown => upsert anyway (safe)
        var dirty = candidates.Where(IsDirtyOrUnknown).ToList();
        if (dirty.Count == 0)
            return;

        var batch = new SqlBatch();

        foreach (var group in dirty.GroupBy(x => x.GetType()))
        {
            ct.ThrowIfCancellationRequested();

            var modelType = group.Key;
            var models = group.ToList();
            if (models.Count == 0)
                continue;

            var doc = VaultDocument.From(modelType);

            var sql = PgDocSql.BuildUpsertSqlMany(modelType, doc, models.Count);
            var args = PgDocSql.GetUpsertParametersMany(doc, models);

            batch.Add(sql, args);
        }

        var (sqlAll, parameters) = batch.Build();
        await _db.ExecuteAsync(sqlAll, parameters!).ConfigureAwait(false);

        foreach (var model in dirty)
            AcceptChangesIfPossible(model);

        // ---------------- local helpers ----------------

        void AddComponentValue(object value)
        {
            // Single
            if (value is IVaultModel vmSingle)
            {
                Add(vmSingle);
                return;
            }

            // Collection
            if (value is IEnumerable en)
            {
                foreach (var it in en)
                {
                    if (it is IVaultModel vm)
                        Add(vm);
                }
            }
        }
    }

    private static bool IsDirtyOrUnknown(IVaultModel model)
    {
        if (model is IChangeTracking ct)
            return ct.IsChanged;

        var t = model.GetType();
        var p = t.GetProperty("IsDirty") ?? t.GetProperty("Dirty");
        if (p is not null && p.PropertyType == typeof(bool) && p.GetMethod is not null)
        {
            try
            { return (bool)p.GetValue(model)!; }
            catch { return true; }
        }

        return true;
    }

    private static void AcceptChangesIfPossible(IVaultModel model)
    {
        var t = model.GetType();
        var m = t.GetMethod("AcceptChanges", Type.EmptyTypes);
        if (m is not null && m.ReturnType == typeof(void))
        {
            try
            { m.Invoke(model, null); }
            catch { /* ignore */ }
        }
    }

    private sealed class SqlBatch
    {
        private readonly List<string> _statements = new();
        private readonly List<object> _parameters = new();

        public void Add(string sql, IReadOnlyList<object?> parameters)
        {
            if (string.IsNullOrWhiteSpace(sql))
                return;

            sql = sql.Trim();
            if (!sql.EndsWith(";", StringComparison.Ordinal))
                sql += ";";

            _statements.Add(sql);

            foreach (var p in parameters)
                _parameters.Add(p ?? DBNull.Value);
        }

        public (string Sql, List<object> Parameters) Build()
        {
            var sb = new System.Text.StringBuilder();
            foreach (var s in _statements)
                sb.AppendLine(s);

            return (sb.ToString(), _parameters);
        }
    }

    /// <summary>
    /// Small SQL glue around Document.From. Not a provider. No DI.
    /// </summary>
    private static class PgDocSql
    {
        private const string DefaultPkLogical = nameof(IVaultModel.StorageId);

        public static string BuildUpsertSqlMany(Type modelType, VaultDocument doc, int rowCount)
        {
            if (rowCount <= 0)
                throw new ArgumentOutOfRangeException(nameof(rowCount));

            var table = QualifiedTable(modelType, doc);

            // conflict target: StorageId column
            var pkCol = Quote(Col(doc, DefaultPkLogical));

            var logicalFields = doc.Fields;
            if (logicalFields is null || logicalFields.Count == 0)
                throw new InvalidOperationException($"{modelType.Name} Document has no fields.");

            var physicalCols = logicalFields.Select(f => Quote(Col(doc, f))).ToList();

            var colsCsv = string.Join(", ", physicalCols);

            // VALUES (...), (...), ...
            var tuple = "(" + string.Join(", ", Enumerable.Repeat("?", physicalCols.Count)) + ")";
            var valuesCsv = string.Join(", ", Enumerable.Repeat(tuple, rowCount));

            // Update all except PK
            var updates = new List<string>();
            for (int i = 0; i < logicalFields.Count; i++)
            {
                var logical = logicalFields[i];
                if (string.Equals(logical, DefaultPkLogical, StringComparison.Ordinal))
                    continue;

                var col = Quote(Col(doc, logical));
                updates.Add($"{col} = EXCLUDED.{col}");
            }

            var updateSql = updates.Count == 0 ? "NOTHING" : string.Join(", ", updates);

            return $"INSERT INTO {table} ({colsCsv}) VALUES {valuesCsv} " +
                   $"ON CONFLICT ({pkCol}) DO UPDATE SET {updateSql}";
        }

        public static IReadOnlyList<object?> GetUpsertParametersMany(VaultDocument doc, IReadOnlyList<IVaultModel> models)
        {
            var all = new List<object?>(capacity: doc.Fields.Count * models.Count);
            foreach (var m in models)
            {
                var row = GetUpsertParameters(doc, m);
                for (int i = 0; i < row.Count; i++)
                    all.Add(row[i]);
            }
            return all;
        }

        public static IReadOnlyList<object?> GetUpsertParameters(VaultDocument doc, object model)
        {
            var logicalFields = doc.Fields;
            var accessors = doc.PropertyAccessors;

            var args = new object?[logicalFields.Count];

            for (int i = 0; i < logicalFields.Count; i++)
            {
                var logical = logicalFields[i];

                if (accessors.TryGetValue(logical, out var acc))
                    args[i] = acc(model);
                else
                {
                    var p = model.GetType().GetProperty(logical, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    args[i] = p?.GetValue(model);
                }
            }

            return args;
        }

        public static string QualifiedTable(Type modelType, VaultDocument doc)
        {
            var va = modelType.GetCustomAttribute<VaultAttribute>(inherit: true);
            var schema = string.IsNullOrWhiteSpace(va?.Keyspace) ? "public" : va!.Keyspace!.Trim();
            return $"{Quote(schema)}.{Quote(doc.Name)}";
        }

        public static string Col(VaultDocument doc, string logical)
            => doc.Columns.TryGetValue(logical, out var physical)
                ? physical
                : VaultDocument.ToCamelCase(logical);

        private static string Quote(string s) => $"\"{s.Replace("\"", "\"\"")}\"";
    }
}
