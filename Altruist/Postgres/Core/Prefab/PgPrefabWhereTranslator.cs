using System.Linq.Expressions;

using Altruist.Persistence.Postgres;

namespace Altruist.Persistence;

internal sealed class PgPrefabWhereTranslator
{
    private readonly IPgModelSqlMetadataProvider _sqlMeta;
    private readonly PrefabMeta _prefab;

    public PgPrefabWhereTranslator(IPgModelSqlMetadataProvider sqlMeta, PrefabMeta prefab)
    {
        _sqlMeta = sqlMeta;
        _prefab = prefab;
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

            MethodCallExpression mc
                => VisitMethodCall(mc),

            _ => throw new NotSupportedException($"Unsupported prefab WHERE expression: {expr.NodeType} / {expr}")
        };
    }

    private string VisitMethodCall(MethodCallExpression mc)
    {
        // collection.Any(x => predicate)
        if (mc.Method.Name == nameof(Enumerable.Any) && mc.Arguments.Count == 2)
        {
            // first arg is collection property
            var collectionExpr = mc.Arguments[0];
            var predicate = (LambdaExpression)StripQuotes(mc.Arguments[1]);

            var (collectionName, meta) = ResolveCollectionComponent(collectionExpr);

            // No nesting: principal is always root
            var rootSql = _sqlMeta.Get(_prefab.RootComponentType);
            var depSql = _sqlMeta.Get(meta.ComponentType);

            var depDoc = depSql.Document;

            var fkLogical = meta.ForeignKeyPropertyName;
            var fkPhysical = depDoc.Columns.TryGetValue(fkLogical, out var fkPhys)
                ? fkPhys
                : Document.ToCamelCase(fkLogical);

            // Translate predicate over dependent row alias "d"
            var predSql = new DependentPredicateTranslator(_sqlMeta, depSql.ModelType).Translate(predicate, "d");

            // principal key is root StorageId (assumed)
            var rootDoc = rootSql.Document;
            var rootPkPhysical = rootDoc.Columns.TryGetValue(nameof(IVaultModel.StorageId), out var rpk)
                ? rpk
                : Document.ToCamelCase(nameof(IVaultModel.StorageId));

            return
                $"EXISTS (SELECT 1 FROM {depSql.QualifiedTable} d " +
                $"WHERE d.\"{fkPhysical}\" = r.\"{rootPkPhysical}\" AND ({predSql}))";
        }

        throw new NotSupportedException($"Unsupported method call in prefab WHERE: {mc.Method.Name}");
    }

    private string VisitComparison(BinaryExpression b)
    {
        // left could be p.Character.Name or p.Character.StorageId etc.
        if (TryResolveRootMember(b.Left, out var rootMember))
        {
            var val = ExpressionUtils.Evaluate(b.Right);
            return $"r.\"{rootMember}\" {Op(b.NodeType)} {ToSqlLiteral(val)}";
        }

        if (TryResolveRootMember(b.Right, out var rootMember2))
        {
            var val = ExpressionUtils.Evaluate(b.Left);
            return $"r.\"{rootMember2}\" {Op(b.NodeType)} {ToSqlLiteral(val)}";
        }

        throw new NotSupportedException("Prefab WHERE supports only root member comparisons and collection Any(...).");
    }

    private bool TryResolveRootMember(Expression expr, out string physicalColumn)
    {
        physicalColumn = "";

        expr = StripConvert(expr);

        // Expect: p.<RootProperty>.<Member>
        if (expr is not MemberExpression leaf)
            return false;

        if (leaf.Expression is not MemberExpression owner)
            return false;

        if (owner.Member.Name != _prefab.RootPropertyName)
            return false;

        var rootSql = _sqlMeta.Get(_prefab.RootComponentType);
        var rootDoc = rootSql.Document;

        var logical = leaf.Member.Name;
        physicalColumn = rootDoc.Columns.TryGetValue(logical, out var phys)
            ? phys
            : Document.ToCamelCase(logical);

        return true;
    }

    private (string Name, PrefabComponentMeta Meta) ResolveCollectionComponent(Expression expr)
    {
        expr = StripConvert(expr);

        // Expect: p.<CollectionProperty>
        if (expr is MemberExpression me)
        {
            var name = me.Member.Name;

            if (!_prefab.ComponentsByName.TryGetValue(name, out var meta))
                throw new InvalidOperationException($"Unknown prefab component '{name}'.");

            if (meta.Kind != PrefabComponentKind.Collection)
                throw new InvalidOperationException($"'{name}' is not a collection component.");

            return (name, meta);
        }

        throw new NotSupportedException("Any() must be called on a prefab collection component property.");
    }

    private static Expression StripConvert(Expression e)
    {
        while (e is UnaryExpression u &&
               (u.NodeType == ExpressionType.Convert || u.NodeType == ExpressionType.ConvertChecked))
            e = u.Operand;
        return e;
    }

    private static Expression StripQuotes(Expression e)
    {
        while (e is UnaryExpression u && u.NodeType == ExpressionType.Quote)
            e = u.Operand;
        return e;
    }

    private static bool IsComparison(ExpressionType t)
        => t is ExpressionType.Equal or ExpressionType.NotEqual;

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

    /// <summary>
    /// Very small translator for "e => e.Prop == value" inside Any(...).
    /// Supports AND/OR and ==/!= on dependent members.
    /// </summary>
    private sealed class DependentPredicateTranslator
    {
        private readonly IPgModelSqlMetadataProvider _sqlMeta;
        private readonly Type _depType;

        public DependentPredicateTranslator(IPgModelSqlMetadataProvider sqlMeta, Type depType)
        {
            _sqlMeta = sqlMeta;
            _depType = depType;
        }

        public string Translate(LambdaExpression lambda, string alias)
            => Visit(lambda.Body, alias);

        private string Visit(Expression expr, string alias)
        {
            return expr switch
            {
                UnaryExpression u when u.NodeType is ExpressionType.Convert or ExpressionType.ConvertChecked
                    => Visit(u.Operand, alias),

                BinaryExpression b when b.NodeType == ExpressionType.AndAlso
                    => $"({Visit(b.Left, alias)}) AND ({Visit(b.Right, alias)})",

                BinaryExpression b when b.NodeType == ExpressionType.OrElse
                    => $"({Visit(b.Left, alias)}) OR ({Visit(b.Right, alias)})",

                BinaryExpression b when IsComparison(b.NodeType)
                    => VisitComparison(b, alias),

                _ => throw new NotSupportedException($"Unsupported Any(...) predicate: {expr}")
            };
        }

        private string VisitComparison(BinaryExpression b, string alias)
        {
            if (TryResolveDependentMember(b.Left, out var col))
            {
                var val = ExpressionUtils.Evaluate(b.Right);
                return $"{alias}.\"{col}\" {Op(b.NodeType)} {ToSqlLiteral(val)}";
            }

            if (TryResolveDependentMember(b.Right, out var col2))
            {
                var val = ExpressionUtils.Evaluate(b.Left);
                return $"{alias}.\"{col2}\" {Op(b.NodeType)} {ToSqlLiteral(val)}";
            }

            throw new NotSupportedException("Any(...) predicate must compare dependent member to a constant/captured value.");
        }

        private bool TryResolveDependentMember(Expression expr, out string physicalColumn)
        {
            physicalColumn = "";
            expr = StripConvert(expr);

            if (expr is not MemberExpression me)
                return false;

            // e.Prop
            if (me.Expression is not ParameterExpression)
                return false;

            var depSql = _sqlMeta.Get(_depType);
            var depDoc = depSql.Document;

            var logical = me.Member.Name;
            physicalColumn = depDoc.Columns.TryGetValue(logical, out var phys)
                ? phys
                : Document.ToCamelCase(logical);

            return true;
        }
    }
}

internal static class ExpressionUtils
{
    public static object? Evaluate(Expression expr)
    {
        expr = StripConvert(expr);

        if (expr is ConstantExpression c)
            return c.Value;

        var lambda = Expression.Lambda<Func<object?>>(
            Expression.Convert(expr, typeof(object)));
        return lambda.Compile().Invoke();
    }

    private static Expression StripConvert(Expression e)
    {
        while (e is UnaryExpression u &&
               (u.NodeType == ExpressionType.Convert || u.NodeType == ExpressionType.ConvertChecked))
            e = u.Operand;
        return e;
    }
}
