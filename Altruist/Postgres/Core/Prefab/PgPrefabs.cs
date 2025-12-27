using System.Collections;
using System.ComponentModel;
using System.Reflection;

using Altruist.UORM;

namespace Altruist.Persistence;

[Service(typeof(IPrefabs))]
[ConditionalOnConfig("altruist:persistence:database:provider", havingValue: "postgres")]
public sealed class PgPrefabs : IPrefabs
{
    private readonly ISqlDatabaseProvider _db;

    public PgPrefabs(ISqlDatabaseProvider db)
    {
        _db = db;
    }

    public IPrefabQuery<TPrefab> Query<TPrefab>()
        where TPrefab : PrefabModel, new()
        => new PgPrefabQuery<TPrefab>(_db);

    public async Task SaveAsync(PrefabModel prefab, CancellationToken ct = default)
    {
        if (prefab is null)
            throw new ArgumentNullException(nameof(prefab));

        ct.ThrowIfCancellationRequested();

        var meta = PrefabDocument.Get(prefab.GetType());

        // Dedupe by (Type, StorageId)
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

        // Root (required)
        var rootObj = meta.ComponentsByName[meta.RootPropertyName].Property.GetValue(prefab) as IVaultModel
            ?? throw new InvalidOperationException(
                $"Prefab root '{meta.RootPropertyName}' is null (or not IVaultModel) on {prefab.GetType().Name}.");

        Add(rootObj);

        // Refs currently present on the prefab (if you didn't Include/hydrate, they may be null, that's fine)
        foreach (var comp in meta.ComponentsByName.Values)
        {
            if (comp.Kind == PrefabComponentKind.Root)
                continue;

            var value = comp.Property.GetValue(prefab);
            if (value is null)
                continue;

            if (comp.Kind == PrefabComponentKind.Single)
            {
                if (value is IVaultModel vm)
                    Add(vm);
                continue;
            }

            if (comp.Kind == PrefabComponentKind.Collection)
            {
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

        if (candidates.Count == 0)
            return;

        // Best-effort dirty detection; if unknown => upsert anyway (safe)
        var dirty = candidates.Where(IsDirtyOrUnknown).ToList();
        if (dirty.Count == 0)
            return;

        var batch = new SqlBatch();

        foreach (var model in dirty)
        {
            ct.ThrowIfCancellationRequested();

            var doc = VaultDocument.From(model.GetType());
            var sql = PgDocSql.BuildUpsertSql(model.GetType(), doc);
            var args = PgDocSql.GetUpsertParameters(doc, model);

            batch.Add(sql, args);
        }

        var (sqlAll, parameters) = batch.Build();
        await _db.ExecuteAsync(sqlAll, parameters!);

        foreach (var model in dirty)
            AcceptChangesIfPossible(model);
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

        public static string BuildUpsertSql(Type modelType, VaultDocument doc)
        {
            var table = QualifiedTable(modelType, doc);

            // We rely on DocumentBuilder having fields/columns/accessors correct.
            // Use StorageId as the conflict target (your framework identity).
            var pkCol = Quote(Col(doc, DefaultPkLogical));

            var logicalFields = doc.Fields; // logical
            if (logicalFields is null || logicalFields.Count == 0)
                throw new InvalidOperationException($"{modelType.Name} Document has no fields.");

            var physicalCols = logicalFields.Select(f => Quote(Col(doc, f))).ToList();

            var colsCsv = string.Join(", ", physicalCols);
            var valsCsv = string.Join(", ", Enumerable.Repeat("?", physicalCols.Count));

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

            return $"INSERT INTO {table} ({colsCsv}) VALUES ({valsCsv}) " +
                   $"ON CONFLICT ({pkCol}) DO UPDATE SET {updateSql}";
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
                    // fallback: reflection (should rarely happen if DocumentBuilder is correct)
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
