using System.Linq.Expressions;

using Altruist.Persistence;
using Altruist.Persistence.Postgres;

namespace Altruist.Gaming.Prefabs;

public sealed class PgPrefabVault<TPrefab>
    : PgVault<TPrefab>, IPrefabVault<TPrefab>
    where TPrefab : PrefabModel, new()
{
    public PgPrefabVault(
        ISqlDatabaseProvider provider,
        IKeyspace keyspace,
        Document doc)
        : base(provider, keyspace, doc)
    {
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
        await SaveTrackedComponentsAsync(entity).ConfigureAwait(false);
        await base.SaveAsync(entity, saveHistory).ConfigureAwait(false);
    }

    public new async Task SaveBatchAsync(IEnumerable<TPrefab> entities, bool? saveHistory = false)
    {
        foreach (var e in entities)
            await SaveTrackedComponentsAsync(e).ConfigureAwait(false);

        await base.SaveBatchAsync(entities, saveHistory).ConfigureAwait(false);
    }

    private async Task SaveTrackedComponentsAsync(TPrefab prefab)
    {
        if (prefab is not PrefabModel pm)
            return;

        var bucket = PrefabComponentTracker.GetBucket(pm);
        if (bucket is null || bucket.Components.Count == 0)
            return;

        foreach (var kv in bucket.Components)
        {
            var meta = kv.Key;
            var component = kv.Value;

            await meta.SaveBatchAsync(new[] { component }).ConfigureAwait(false);
            pm.ComponentRefs[meta.Name] = component.StorageId;
        }

        PrefabComponentTracker.Clear(pm);
    }

    public override string ConvertWherePredicateToString(Expression<Func<TPrefab, bool>> predicate)
    {
        var visitor = new PrefabExpressionVisitor(typeof(TPrefab));
        return visitor.Translate(predicate);
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
