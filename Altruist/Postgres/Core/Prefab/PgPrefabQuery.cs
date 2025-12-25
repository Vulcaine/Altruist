using System.Linq.Expressions;
using System.Reflection;

using Altruist.Persistence.Postgres;

namespace Altruist.Persistence;

internal sealed class PgPrefabQuery<TPrefab> : IPrefabQuery<TPrefab>
    where TPrefab : PrefabModel, new()
{
    private readonly ISqlDatabaseProvider _db;
    private readonly IPgModelSqlMetadataProvider _sqlMeta;

    private readonly List<string> _wheres = new();
    private readonly HashSet<string> _includes = new(StringComparer.Ordinal);

    private int? _skip;
    private int? _take;

    public PgPrefabQuery(ISqlDatabaseProvider db, IPgModelSqlMetadataProvider sqlMeta)
    {
        _db = db;
        _sqlMeta = sqlMeta;
    }

    public IPrefabQuery<TPrefab> Where(Expression<Func<TPrefab, bool>> predicate)
    {
        var meta = PrefabDocument.Get(typeof(TPrefab));
        var translator = new PgPrefabWhereTranslator(_sqlMeta, meta);
        _wheres.Add(translator.Translate(predicate));
        return this;
    }

    public IPrefabQuery<TPrefab> Include<TProp>(Expression<Func<TPrefab, TProp>> selector)
    {
        var name = ResolveIncludeName(selector.Body);
        _includes.Add(name);
        return this;
    }

    private IPrefabQuery<TPrefab> Skip(int count) { _skip = count; return this; }
    private IPrefabQuery<TPrefab> Take(int count) { _take = count; return this; }

    public async Task<List<TPrefab>> ToListAsync(CancellationToken ct = default)
    {
        var prefabMeta = PrefabDocument.Get(typeof(TPrefab));

        var rootSql = _sqlMeta.Get(prefabMeta.RootComponentType);
        var rootTable = rootSql.QualifiedTable;

        // Root query: SELECT r.* FROM root r WHERE ...
        var sql = $"SELECT r.* FROM {rootTable} r";

        if (_wheres.Count > 0)
            sql += " WHERE " + string.Join(" AND ", _wheres.Select(w => $"({w})"));

        if (_skip.HasValue && _skip.Value > 0)
            sql += $" OFFSET {_skip.Value}";

        if (_take.HasValue)
            sql += $" LIMIT {_take.Value}";

        // Load root rows strongly typed
        var roots = await QueryTypedList(rootSql.ModelType, sql, parameters: Array.Empty<object?>()).ConfigureAwait(false);
        if (roots.Count == 0)
            return new List<TPrefab>();

        // Construct prefabs
        var list = new List<TPrefab>(roots.Count);
        foreach (var root in roots)
        {
            var p = new TPrefab();
            SetProperty(p, prefabMeta.RootPropertyName, root);
            list.Add(p);
        }

        // Hydrate includes
        if (_includes.Count == 0)
            return list;

        var loader = new PgPrefabEagerLoader(_db, _sqlMeta, prefabMeta);
        await loader.HydrateAsync(list, _includes, ct).ConfigureAwait(false);

        return list;
    }

    public async Task<TPrefab?> FirstOrDefaultAsync(CancellationToken ct = default)
    {
        Take(1);
        var list = await ToListAsync(ct).ConfigureAwait(false);
        return list.FirstOrDefault();
    }

    private static string ResolveIncludeName(Expression expr)
    {
        while (expr is UnaryExpression u &&
               (u.NodeType == ExpressionType.Convert || u.NodeType == ExpressionType.ConvertChecked))
            expr = u.Operand;

        if (expr is MemberExpression me)
            return me.Member.Name;

        throw new NotSupportedException("Include selector must be a component property, e.g. p => p.Character or p => p.Equipment.");
    }

    private static void SetProperty(object target, string propertyName, object value)
    {
        var p = target.GetType().GetProperty(propertyName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException($"Property '{propertyName}' not found on {target.GetType().Name}.");
        p.SetValue(target, value);
    }

    private async Task<List<object>> QueryTypedList(Type modelType, string sql, object?[] parameters)
    {
        // Try to invoke ISqlDatabaseProvider.QueryAsync<T>(string) or QueryAsync<T>(string, object?[])
        var methods = _db.GetType().GetMethods(BindingFlags.Instance | BindingFlags.Public);

        var candidates = methods
            .Where(m => m.Name == nameof(ISqlDatabaseProvider.QueryAsync) && m.IsGenericMethodDefinition)
            .ToList();

        if (candidates.Count == 0)
            throw new InvalidOperationException("No QueryAsync<T> method found on ISqlDatabaseProvider implementation.");

        MethodInfo? chosen = null;
        object? taskObj = null;

        foreach (var m in candidates)
        {
            var gm = m.MakeGenericMethod(modelType);
            var ps = gm.GetParameters();

            try
            {
                if (ps.Length == 1)
                {
                    chosen = gm;
                    taskObj = gm.Invoke(_db, new object?[] { sql });
                    break;
                }

                if (ps.Length == 2)
                {
                    chosen = gm;
                    taskObj = gm.Invoke(_db, new object?[] { sql, parameters });
                    break;
                }
            }
            catch
            {
                // try next overload
            }
        }

        if (chosen is null || taskObj is null)
            throw new InvalidOperationException("Could not invoke QueryAsync<T> with supported parameter shapes.");

        var task = (Task)taskObj;
        await task.ConfigureAwait(false);

        var resultProp = taskObj.GetType().GetProperty("Result")
                         ?? throw new InvalidOperationException("QueryAsync Task has no Result.");

        var enumerable = (System.Collections.IEnumerable)resultProp.GetValue(taskObj)!;

        var list = new List<object>();
        foreach (var it in enumerable)
            list.Add(it!);

        return list;
    }
}
