using System.Linq.Expressions;

using Altruist.Persistence;
using Altruist.Persistence.Postgres;

namespace Altruist.Gaming.Prefabs;



public sealed class PgPrefabVault<TPrefab> : PgVault<TPrefab>, IPrefabVault<TPrefab>
    where TPrefab : PrefabModel
{
    public PgPrefabVault(
        ISqlDatabaseProvider provider,
        IKeyspace keyspace,
        Document doc,
        IServiceProvider services)
        : base(provider, keyspace, doc, services)
    {
    }

    protected override PgVault<TPrefab> Create(QueryState state)
    => new PgPrefabVault<TPrefab>(_databaseProvider, Keyspace, VaultDocument, Services, state);

    private PgPrefabVault(
       ISqlDatabaseProvider provider,
       IKeyspace keyspace,
       Document doc,
       IServiceProvider services,
       QueryState state)
       : base(provider, keyspace, doc, services, state)
    {
    }


    public override string ConvertWherePredicateToString(Expression<Func<TPrefab, bool>> predicate)
    {
        var visitor = new PrefabExpressionVisitor(typeof(TPrefab));
        var sql = visitor.Translate(predicate);

        // If visitor throws for non-prefab expressions, you can decide to
        // fall back to base here for plain model-level predicates.
        return sql;
    }

    public override async Task<List<TPrefab>> ToListAsync()
    {
        var list = await base.ToListAsync().ConfigureAwait(false);
        foreach (var p in list)
            PrefabHandleInitializer.InitializeHandles(p!, Services);
        return list;
    }

    public override async Task<TPrefab?> FirstOrDefaultAsync()
    {
        var p = await base.FirstOrDefaultAsync().ConfigureAwait(false);
        if (p != null)
            PrefabHandleInitializer.InitializeHandles(p, Services);
        return p;
    }

    public override async Task<TPrefab?> FirstAsync()
    {
        var p = await base.FirstAsync().ConfigureAwait(false);
        if (p != null)
            PrefabHandleInitializer.InitializeHandles(p, Services);
        return p;
    }

    public override async Task<List<TPrefab>> ToListAsync(Expression<Func<TPrefab, bool>> predicate)
    {
        var list = await base.ToListAsync(predicate).ConfigureAwait(false);
        foreach (var p in list)
            PrefabHandleInitializer.InitializeHandles(p!, Services);
        return list;
    }

    public override async Task SaveAsync(TPrefab entity, bool? saveHistory = false)
    {
        await SaveTrackedComponentsAsync(entity).ConfigureAwait(false);
        await base.SaveAsync(entity, saveHistory).ConfigureAwait(false);
    }

    public override async Task SaveBatchAsync(IEnumerable<TPrefab> entities, bool? saveHistory = false)
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

            await meta.SaveBatchAsync(
                _serviceProvider,
                [component]
            ).ConfigureAwait(false);

            pm.ComponentRefs[meta.Name] = component.StorageId;
        }

        PrefabComponentTracker.Clear(pm);
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
    {
        return Visit(lambda.Body);
    }

    private string Visit(Expression expr)
    {
        switch (expr)
        {
            case UnaryExpression u when u.NodeType is ExpressionType.Convert or ExpressionType.ConvertChecked:
                return Visit(u.Operand);

            case BinaryExpression b when IsComparison(b.NodeType):
                return VisitComparison(b);

            case BinaryExpression b when b.NodeType == ExpressionType.AndAlso:
                return $"({Visit(b.Left)}) AND ({Visit(b.Right)})";

            case BinaryExpression b when b.NodeType == ExpressionType.OrElse:
                return $"({Visit(b.Left)}) OR ({Visit(b.Right)})";

            default:
                throw new NotSupportedException($"Unsupported expression in prefab WHERE: {expr}");
        }
    }

    private string VisitComparison(BinaryExpression b)
    {
        // Pattern: x.Component.Id == constant
        if (TryResolveComponentAccess(b.Left, out var compKey))
        {
            var val = ExpressionUtils.Evaluate(b.Right);
            return $"(component-refs->>'{compKey}') {Op(b.NodeType)} {ToSqlLiteral(val)}";
        }

        if (TryResolveComponentAccess(b.Right, out var compKey2))
        {
            var val = ExpressionUtils.Evaluate(b.Left);
            // flip operator when swapping sides if you ever support >/< etc.
            return $"(component-refs->>'{compKey2}') {Op(Flip(b.NodeType))} {ToSqlLiteral(val)}";
        }

        throw new NotSupportedException(
            "Comparison must involve a prefab component access like x.Component.Id == constant.");
    }

    private bool TryResolveComponentAccess(Expression expr, out string compName)
    {
        compName = "";

        // Strip casts
        while (expr is UnaryExpression u &&
               (u.NodeType == ExpressionType.Convert || u.NodeType == ExpressionType.ConvertChecked))
        {
            expr = u.Operand;
        }

        if (expr is MemberExpression me &&
            me.Expression is MemberExpression owner)
        {
            var componentName = owner.Member.Name;

            if (PrefabMetadataRegistry
                .GetComponents(_prefabType)
                .Any(c => c.Name == componentName))
            {
                compName = componentName;
                return true;
            }
        }

        return false;
    }

    private static bool IsComparison(ExpressionType t) =>
        t is ExpressionType.Equal or ExpressionType.NotEqual
          or ExpressionType.GreaterThan or ExpressionType.GreaterThanOrEqual
          or ExpressionType.LessThan or ExpressionType.LessThanOrEqual;

    private static string Op(ExpressionType t) => t switch
    {
        ExpressionType.Equal => "=",
        ExpressionType.NotEqual => "!=",
        _ => throw new NotSupportedException("Only == and != are supported for prefab component ids.")
    };

    private static ExpressionType Flip(ExpressionType t) => t switch
    {
        ExpressionType.GreaterThan => ExpressionType.LessThan,
        ExpressionType.GreaterThanOrEqual => ExpressionType.LessThanOrEqual,
        ExpressionType.LessThan => ExpressionType.GreaterThan,
        ExpressionType.LessThanOrEqual => ExpressionType.GreaterThanOrEqual,
        _ => t
    };

    private static string ToSqlLiteral(object? value)
        => value switch
        {
            null => "NULL",
            string s => $"'{s.Replace("'", "''")}'",
            bool b => b ? "TRUE" : "FALSE",
            DateTime dt => $"'{dt:yyyy-MM-dd HH:mm:ss}'",
            Enum e => $"'{e.ToString().Replace("'", "''")}'",
            _ => $"'{value?.ToString()?.Replace("'", "''")}'"
        };
}
