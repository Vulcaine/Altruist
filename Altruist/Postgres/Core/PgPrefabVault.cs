// PgPrefabVault.cs

using System.Linq.Expressions;
using System.Text.Json;

using Altruist.Persistence;
using Altruist.Persistence.Postgres;

namespace Altruist.Gaming.Prefabs;

public sealed class PgPrefabVault<TPrefab>
    : PgVault<TPrefab>, IPrefabVault<TPrefab>
    where TPrefab : PrefabModel, new()
{
    private readonly IPgModelSqlMetadataProvider _sqlMeta;

    public PgPrefabVault(
        ISqlDatabaseProvider provider,
        IKeyspace keyspace,
        Document doc,
        IPgModelSqlMetadataProvider sqlMeta)
        : base(provider, keyspace, doc)
    {
        _sqlMeta = sqlMeta;
    }

    public TPrefab Construct()
    {
        var prefab = new TPrefab();
        PrefabHandleInitializer.InitializeHandles(prefab, Dependencies.RootProvider!);
        return prefab;
    }

    public new async Task<List<TPrefab>> ToListAsync()
    {
        var list = await base.ToListAsync().ConfigureAwait(false);
        var sp = Dependencies.RootProvider!;

        foreach (var p in list)
            PrefabHandleInitializer.InitializeHandles(p, sp);

        return list;
    }

    public new async Task<List<TPrefab>> ToListAsync(Expression<Func<TPrefab, bool>> predicate)
    {
        var list = await base.ToListAsync(predicate).ConfigureAwait(false);
        var sp = Dependencies.RootProvider!;

        foreach (var p in list)
            PrefabHandleInitializer.InitializeHandles(p, sp);

        return list;
    }

    public new async Task<TPrefab?> FirstOrDefaultAsync()
    {
        var p = await base.FirstOrDefaultAsync().ConfigureAwait(false);
        if (p != null)
            PrefabHandleInitializer.InitializeHandles(p, Dependencies.RootProvider!);
        return p;
    }

    public new async Task<TPrefab?> FirstAsync()
    {
        var p = await base.FirstAsync().ConfigureAwait(false);
        if (p != null)
            PrefabHandleInitializer.InitializeHandles(p, Dependencies.RootProvider!);
        return p;
    }

    public new async Task SaveAsync(TPrefab entity, bool? saveHistory = false)
    {
        if (entity is not PrefabModel pm)
        {
            await base.SaveAsync(entity, saveHistory).ConfigureAwait(false);
            return;
        }

        var bucket = PrefabComponentTracker.GetBucket(pm);

        if (bucket is null || bucket.Components.Count == 0)
        {
            await base.SaveAsync(entity, saveHistory).ConfigureAwait(false);
            return;
        }

        var batch = new PgSqlBatchBuilder();

        foreach (var kv in bucket.Components)
        {
            var meta = kv.Key;
            var component = kv.Value;

            if (string.IsNullOrWhiteSpace(component.StorageId))
                throw new InvalidOperationException(
                    $"Tracked component '{meta.Name}' ({component.GetType().Name}) has empty StorageId.");

            pm.ComponentRefs[meta.Name] = component.StorageId;

            var sqlModel = _sqlMeta.Get(component.GetType());
            batch.Add(sqlModel.UpsertSql, sqlModel.GetUpsertParameters(component));
        }

        {
            var sqlPrefab = _sqlMeta.Get(typeof(TPrefab));
            batch.Add(sqlPrefab.UpsertSql, sqlPrefab.GetUpsertParameters(pm));
        }

        var (sql, parameters) = batch.BuildSql();
        await DatabaseProvider.ExecuteAsync(sql, parameters).ConfigureAwait(false);

        PrefabComponentTracker.Clear(pm);
    }

    public new async Task SaveBatchAsync(IEnumerable<TPrefab> entities, bool? saveHistory = false)
    {
        var list = entities as IList<TPrefab> ?? entities.ToList();
        if (list.Count == 0)
            return;

        var batch = new PgSqlBatchBuilder();
        var cleared = new List<PrefabModel>(list.Count);

        foreach (var p in list)
        {
            if (p is not PrefabModel pm)
                continue;

            var bucket = PrefabComponentTracker.GetBucket(pm);
            if (bucket is null || bucket.Components.Count == 0)
                continue;

            foreach (var kv in bucket.Components)
            {
                var meta = kv.Key;
                var component = kv.Value;

                if (string.IsNullOrWhiteSpace(component.StorageId))
                    throw new InvalidOperationException(
                        $"Tracked component '{meta.Name}' ({component.GetType().Name}) has empty StorageId.");

                pm.ComponentRefs[meta.Name] = component.StorageId;

                var sqlModel = _sqlMeta.Get(component.GetType());
                batch.Add(sqlModel.UpsertSql, sqlModel.GetUpsertParameters(component));
            }

            cleared.Add(pm);
        }

        foreach (var p in list)
        {
            var sqlPrefab = _sqlMeta.Get(typeof(TPrefab));
            batch.Add(sqlPrefab.UpsertSql, sqlPrefab.GetUpsertParameters(p));
        }

        var (sql, parameters) = batch.BuildSql();
        await DatabaseProvider.ExecuteAsync(sql, parameters).ConfigureAwait(false);

        foreach (var pm in cleared)
            PrefabComponentTracker.Clear(pm);
    }

    public async Task LoadAllComponentsAsync(TPrefab prefab, CancellationToken ct = default)
    {
        if (prefab is not PrefabModel pm)
            return;

        var metas = PrefabMetadataRegistry.GetComponents(prefab.GetType());
        if (metas.Count == 0)
            return;

        var metaByName = new Dictionary<string, PrefabComponentMetadata>(StringComparer.Ordinal);
        foreach (var m in metas)
            metaByName[m.Name] = m;

        var selects = new List<string>(metas.Count);
        var parameters = new List<object>(metas.Count);

        foreach (var meta in metas)
        {
            if (!pm.ComponentRefs.TryGetValue(meta.Name, out var id) || string.IsNullOrWhiteSpace(id))
                continue;

            var sqlModel = _sqlMeta.Get(meta.ComponentType);

            var doc = sqlModel.Document;
            var idCol = doc.Columns.TryGetValue(nameof(IVaultModel.StorageId), out var c)
                ? c
                : Document.ToCamelCase(nameof(IVaultModel.StorageId));

            selects.Add(
                $"SELECT {SqlLiteral(meta.Name)} AS \"Component\", to_jsonb(t) AS \"Json\" " +
                $"FROM {sqlModel.QualifiedTable} t WHERE t.{Quote(idCol)} = ?");

            parameters.Add(id);
        }

        if (selects.Count == 0)
            return;

        var sql = string.Join(" UNION ALL ", selects);

        var rows = (await DatabaseProvider
                .QueryAsync<PrefabComponentFetchRow>(sql, parameters)
                .ConfigureAwait(false))
            .ToList();

        if (rows.Count == 0)
            return;

        var jsonOptions = Dependencies.Inject<JsonSerializerOptions>();

        var loaded = new List<(PrefabComponentMetadata Meta, IVaultModel Component)>(rows.Count);

        foreach (var row in rows)
        {
            if (!metaByName.TryGetValue(row.Component, out var meta))
                continue;

            var json = ExtractJson(row.Json);
            if (string.IsNullOrWhiteSpace(json))
                continue;

            var obj = meta.DeserializeJson(json!, jsonOptions);
            if (obj is null)
                continue;

            var handleObj = meta.Getter(prefab);
            if (handleObj is null)
                continue;

            meta.ApplyBulkToHandle(handleObj, obj);
            loaded.Add((meta, obj));
        }

        foreach (var (meta, comp) in loaded)
            await PrefabComponentLifecycle.OnComponentLoadedAsync(pm, meta, comp, allowAutoLoad: false)
                .ConfigureAwait(false);
    }

    public override string ConvertWherePredicateToString(Expression<Func<TPrefab, bool>> predicate)
    {
        var visitor = new PrefabExpressionVisitor(typeof(TPrefab));
        return visitor.Translate(predicate);
    }

    private static string Quote(string s) => $"\"{s.Replace("\"", "\"\"")}\"";
    private static string SqlLiteral(string s) => $"'{s.Replace("'", "''")}'";

    private static string? ExtractJson(object? jsonValue)
    {
        if (jsonValue is null)
            return null;

        if (jsonValue is string s)
            return s;

        if (jsonValue is JsonDocument doc)
            return doc.RootElement.GetRawText();

        if (jsonValue is JsonElement el)
            return el.GetRawText();

        return jsonValue.ToString();
    }

    private sealed class PrefabComponentFetchRow
    {
        public string Component { get; set; } = "";
        public object? Json { get; set; }
    }

    private sealed class PgSqlBatchBuilder
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

        public (string Sql, List<object> Parameters) BuildSql()
        {
            var sb = new System.Text.StringBuilder();
            foreach (var s in _statements)
                sb.AppendLine(s);
            return (sb.ToString(), _parameters);
        }
    }
}


internal sealed class PrefabExpressionVisitor
{
    private readonly Type _prefabType;

    public PrefabExpressionVisitor(Type prefabType)
    {
        _prefabType = prefabType;
    }

    public string Translate(LambdaExpression lambda)
        => Visit(lambda.Body);

    private string Visit(Expression expr)
    {
        return expr switch
        {
            UnaryExpression u when u.NodeType is ExpressionType.Convert or ExpressionType.ConvertChecked
                => Visit(u.Operand),

            BinaryExpression b when b.NodeType == ExpressionType.AndAlso
                => $"({Visit(b.Left)}) AND ({Visit(b.Right)})",

            BinaryExpression b when b.NodeType == ExpressionType.OrElse
                => $"({Visit(b.Left)}) OR ({Visit(b.Right)})",

            BinaryExpression b when IsComparison(b.NodeType)
                => VisitComparison(b),

            _ => throw new NotSupportedException($"Unsupported expression in prefab WHERE: {expr}")
        };
    }

    private string VisitComparison(BinaryExpression b)
    {
        if (TryResolveComponentAccess(b.Left, out var comp))
        {
            var val = ExpressionUtils.Evaluate(b.Right);
            return $"(component_refs->>'{comp}') {Op(b.NodeType)} {ToSqlLiteral(val)}";
        }

        if (TryResolveComponentAccess(b.Right, out var comp2))
        {
            var val = ExpressionUtils.Evaluate(b.Left);
            return $"(component_refs->>'{comp2}') {Op(b.NodeType)} {ToSqlLiteral(val)}";
        }

        throw new NotSupportedException("Prefab WHERE must compare against component access.");
    }

    private bool TryResolveComponentAccess(Expression expr, out string compName)
    {
        compName = "";

        while (expr is UnaryExpression u &&
               (u.NodeType == ExpressionType.Convert || u.NodeType == ExpressionType.ConvertChecked))
            expr = u.Operand;

        if (expr is MemberExpression me &&
            me.Expression is MemberExpression owner)
        {
            var candidate = owner.Member.Name;

            if (PrefabMetadataRegistry
                .GetComponents(_prefabType)
                .Any(c => c.Name == candidate))
            {
                compName = candidate;
                return true;
            }
        }

        return false;
    }

    private static bool IsComparison(ExpressionType t) =>
        t is ExpressionType.Equal or ExpressionType.NotEqual;

    private static string Op(ExpressionType t) => t switch
    {
        ExpressionType.Equal => "=",
        ExpressionType.NotEqual => "!=",
        _ => throw new NotSupportedException()
    };

    private static string ToSqlLiteral(object? value) => value switch
    {
        null => "NULL",
        string s => $"'{s.Replace("'", "''")}'",
        bool b => b ? "TRUE" : "FALSE",
        DateTime dt => $"'{dt:yyyy-MM-dd HH:mm:ss}'",
        Enum e => $"'{e.ToString().Replace("'", "''")}'",
        _ => $"'{value!.ToString()!.Replace("'", "''")}'"
    };
}
