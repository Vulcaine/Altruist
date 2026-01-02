using System.Collections.Concurrent;
using System.Linq.Expressions;

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

    // Cache: prefab root setter by property name (compiled once)
    private static readonly ConcurrentDictionary<string, Action<TPrefab, object>> _rootSetterCache =
        new(StringComparer.Ordinal);

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

    public IPrefabQuery<TPrefab> IncludeAll()
    {
        // No reflection here: we rely on PrefabMeta which is already constructed elsewhere.
        var meta = PrefabDocument.Get(typeof(TPrefab));

        foreach (var name in meta.ComponentsByName.Keys)
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

        var rootDoc = VaultDocument.From(prefabMeta.RootComponentType);
        var rootTable = rootDoc.QualifiedTable();

        // Explicit SELECT: r."db-col" AS "ClrProp"
        var select = BuildSelectAllAliased(rootDoc, "r");
        var sql = $"SELECT {select} FROM {rootTable} r";

        if (_wheres.Count > 0)
            sql += " WHERE " + string.Join(" AND ", _wheres.Select(w => $"({w})"));

        if (_skip is > 0)
            sql += $" OFFSET {_skip.Value}";

        if (_take.HasValue)
            sql += $" LIMIT {_take.Value}";

        var roots = await _db.QueryAsync(
            prefabMeta.RootComponentType,
            sql,
            _whereParams,
            ct).ConfigureAwait(false);

        if (roots.Count == 0)
            return new List<TPrefab>();

        var setRoot = GetRootSetter(prefabMeta.RootPropertyName);
        var list = new List<TPrefab>(roots.Count);

        foreach (var root in roots)
        {
            ct.ThrowIfCancellationRequested();

            var p = new TPrefab();
            setRoot(p, root);
            list.Add(p);
        }

        if (_includes.Count == 0)
            return list;

        var loader = new PgPrefabEagerLoader(_db, prefabMeta);
        await loader.HydrateAsync(list, _includes, ct).ConfigureAwait(false);

        return list;
    }

    private static string BuildSelectAllAliased(VaultDocument doc, string alias)
    {
        static string QuoteIdent(string s) => $"\"{s.Replace("\"", "\"\"")}\"";

        // doc.Columns: CLR property name -> SQL column name
        var cols = new List<string>(doc.Columns.Count);
        foreach (var kvp in doc.Columns)
        {
            var clrProp = kvp.Key;
            var sqlCol = kvp.Value;

            cols.Add($"{alias}.{VaultDocument.Quote(sqlCol)} AS {QuoteIdent(clrProp)}");
        }

        return string.Join(", ", cols);
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

        throw new NotSupportedException(
            "Include selector must be a component property, e.g. p => p.Character or p => p.Equipment.");
    }

    private static Action<TPrefab, object> GetRootSetter(string propertyName)
    {
        return _rootSetterCache.GetOrAdd(propertyName, static name =>
        {
            var target = Expression.Parameter(typeof(TPrefab), "target");
            var value = Expression.Parameter(typeof(object), "value");

            var member = Expression.PropertyOrField(target, name);
            var assign = Expression.Assign(member, Expression.Convert(value, member.Type));

            return Expression.Lambda<Action<TPrefab, object>>(assign, target, value).Compile();
        });
    }
}
