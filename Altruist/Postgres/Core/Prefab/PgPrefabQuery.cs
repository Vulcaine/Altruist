using System.Linq.Expressions;
using System.Reflection;

using Altruist.UORM;

namespace Altruist.Persistence;

internal sealed class PgPrefabQuery<TPrefab> : IPrefabQuery<TPrefab>
    where TPrefab : PrefabModel, new()
{
    private readonly ISqlDatabaseProvider _db;

    private readonly List<string> _wheres = new();
    private readonly List<object?> _whereParams = new();
    private readonly HashSet<string> _includes = new(StringComparer.Ordinal);

    private int? _skip;
    private int? _take;

    public PgPrefabQuery(ISqlDatabaseProvider db)
    {
        _db = db;
    }

    public IPrefabQuery<TPrefab> Where(Expression<Func<TPrefab, bool>> predicate)
    {
        var meta = PrefabDocument.Get(typeof(TPrefab));
        var translator = new PgPrefabWhereTranslator(meta);

        var frag = translator.Translate(predicate);

        _wheres.Add(frag.Sql);
        _whereParams.AddRange(frag.Parameters);

        return this;
    }

    public IPrefabQuery<TPrefab> Include<TProp>(Expression<Func<TPrefab, TProp>> selector)
    {
        var name = ResolveIncludeName(selector.Body);
        _includes.Add(name);
        return this;
    }

    // Keep these private (as you requested)
    private PgPrefabQuery<TPrefab> Skip(int count) { _skip = count; return this; }
    private PgPrefabQuery<TPrefab> Take(int count) { _take = count; return this; }

    public async Task<List<TPrefab>> ToListAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        var prefabMeta = PrefabDocument.Get(typeof(TPrefab));

        var rootDoc = Document.From(prefabMeta.RootComponentType);
        var rootTable = PgDocSql.QualifiedTable(prefabMeta.RootComponentType, rootDoc);

        // Root query: SELECT r.* FROM root r WHERE ...
        var sql = $"SELECT r.* FROM {rootTable} r";

        if (_wheres.Count > 0)
            sql += " WHERE " + string.Join(" AND ", _wheres.Select(w => $"({w})"));

        if (_skip is > 0)
            sql += $" OFFSET {_skip.Value}";

        if (_take.HasValue)
            sql += $" LIMIT {_take.Value}";

        // Load root rows strongly typed
        var roots = await QueryTypedList(prefabMeta.RootComponentType, sql, _whereParams).ConfigureAwait(false);
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

        var loader = new PgPrefabEagerLoader(_db, prefabMeta);
        await loader.HydrateAsync(list, _includes, ct);

        return list;
    }

    public async Task<TPrefab?> FirstOrDefaultAsync(CancellationToken ct = default)
    {
        Take(1);
        var list = await ToListAsync(ct);
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

    private async Task<List<object>> QueryTypedList(Type modelType, string sql, List<object?>? parameters)
    {
        // Support common shapes:
        // - QueryAsync<T>(string sql)
        // - QueryAsync<T>(string sql, List<object>? parameters = null)
        // - QueryAsync<T>(string sql, object?[] parameters)
        var methods = _db.GetType().GetMethods(BindingFlags.Instance | BindingFlags.Public);

        var candidates = methods
            .Where(m => m.Name == nameof(ISqlDatabaseProvider.QueryAsync) && m.IsGenericMethodDefinition)
            .ToList();

        if (candidates.Count == 0)
            throw new InvalidOperationException("No QueryAsync<T> method found on ISqlDatabaseProvider implementation.");

        object? taskObj = null;

        foreach (var m in candidates)
        {
            var gm = m.MakeGenericMethod(modelType);
            var ps = gm.GetParameters();

            try
            {
                if (ps.Length == 1)
                {
                    taskObj = gm.Invoke(_db, new object?[] { sql });
                    if (taskObj != null)
                        break;
                }
                else if (ps.Length == 2)
                {
                    var pType = ps[1].ParameterType;

                    // List<object>
                    if (typeof(List<object>).IsAssignableFrom(pType))
                    {
                        var list = parameters?.Select(x => x ?? DBNull.Value).Cast<object>().ToList() ?? null;
                        taskObj = gm.Invoke(_db, new object?[] { sql, list });
                        if (taskObj != null)
                            break;
                    }

                    // object?[]
                    if (pType.IsArray)
                    {
                        var arr = parameters?.ToArray() ?? Array.Empty<object?>();
                        taskObj = gm.Invoke(_db, new object?[] { sql, arr });
                        if (taskObj != null)
                            break;
                    }
                }
            }
            catch
            {
                // try next overload
            }
        }

        if (taskObj is null)
            throw new InvalidOperationException("Could not invoke QueryAsync<T> with supported parameter shapes.");

        var task = (Task)taskObj;
        await task.ConfigureAwait(false);

        var resultProp = taskObj.GetType().GetProperty("Result")
            ?? throw new InvalidOperationException("QueryAsync Task has no Result.");

        var enumerable = (System.Collections.IEnumerable)resultProp.GetValue(taskObj)!;

        var listOut = new List<object>();
        foreach (var it in enumerable)
            listOut.Add(it!);

        return listOut;
    }

    private static class PgDocSql
    {
        public static string QualifiedTable(Type modelType, Document doc)
        {
            var va = modelType.GetCustomAttribute<VaultAttribute>(inherit: true);
            var schema = string.IsNullOrWhiteSpace(va?.Keyspace) ? "public" : va!.Keyspace!.Trim();
            return $"{Quote(schema)}.{Quote(doc.Name)}";
        }

        private static string Quote(string s) => $"\"{s.Replace("\"", "\"\"")}\"";
    }
}
