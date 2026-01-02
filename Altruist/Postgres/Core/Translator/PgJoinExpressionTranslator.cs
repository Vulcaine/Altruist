using System.Linq.Expressions;

using Altruist.Querying;

namespace Altruist.Persistence.Postgres.Querying;

internal static class PgJoinExpressionTranslator
{
    // ---------------- JOIN ----------------

    public static string BuildJoin<TLeft, TRight>(
        PgVault<TLeft> left,
        PgVault<TRight> right,
        LambdaExpression leftKey,
        LambdaExpression rightKey,
        JoinType joinType)
        where TLeft : class, IVaultModel
        where TRight : class, IVaultModel
        => BuildJoinDynamic(left, right, leftKey, rightKey, joinType);

    public static string BuildJoinDynamic(
        object left,
        object right,
        LambdaExpression leftKey,
        LambdaExpression rightKey,
        JoinType joinType)
    {
        var join = joinType switch
        {
            JoinType.Inner => "INNER JOIN",
            JoinType.Left => "LEFT JOIN",
            JoinType.Right => "RIGHT JOIN",
            JoinType.Full => "FULL JOIN",
            _ => throw new ArgumentOutOfRangeException(nameof(joinType))
        };

        var leftCol = Column(leftKey, left);
        var rightCol = Column(rightKey, right);

        return $"{join} {QualifiedTable(right)} ON {leftCol} = {rightCol}";
    }

    // ---------------- SELECT (2..6 params) ----------------

    public static string BuildSelect(
        LambdaExpression? projection,
        object from,
        IReadOnlyList<string> joins,
        IReadOnlyList<string> wheres,
        IReadOnlyDictionary<ParameterExpression, object> paramMap)
    {
        var select = projection is null ? "*" : Select(projection, paramMap);

        var sql = $"SELECT {select} FROM {QualifiedTable(from)}";

        if (joins.Count > 0)
            sql += " " + string.Join(" ", joins);

        if (wheres.Count > 0)
            sql += " WHERE " + string.Join(" AND ", wheres);

        return sql;
    }

    // ---------------- WHERE (N params) ----------------

    public static string Translate(
        LambdaExpression predicate,
        IReadOnlyDictionary<ParameterExpression, object> paramMap)
        => Visit(predicate.Body, paramMap);

    private static string Visit(
        Expression expr,
        IReadOnlyDictionary<ParameterExpression, object> paramMap)
    {
        expr = StripConvert(expr);

        if (expr is BinaryExpression be)
        {
            var op = Operator(be.NodeType);
            return $"({Visit(be.Left, paramMap)} {op} {Visit(be.Right, paramMap)})";
        }

        if (expr is MemberExpression me)
        {
            var rootParam = GetRootParameter(me);
            if (rootParam is not null && paramMap.TryGetValue(rootParam, out var vault))
                return Resolve(me, vault);
        }

        // NOTE: still compiles constants. If you truly want 0 compilation, replace with a constant extractor.
        var value = Expression.Lambda(expr).Compile().DynamicInvoke();
        return FormatValue(value);
    }

    // ---------------- COLUMN ----------------

    public static string Column(LambdaExpression expr, object vault)
    {
        var body = StripConvert(expr.Body);

        if (body is not MemberExpression me)
            throw new NotSupportedException("Join key must be a property access.");

        return Resolve(me, vault);
    }

    private static string Resolve(MemberExpression me, object vault)
    {
        var doc = GetDocument(vault);
        var col = doc.Columns.TryGetValue(me.Member.Name, out var c)
            ? c
            : VaultDocument.ToCamelCase(me.Member.Name);

        return $"{QualifiedTable(vault)}.\"{col}\"";
    }

    // ---------------- HELPERS ----------------

    private static ParameterExpression? GetRootParameter(MemberExpression me)
    {
        Expression? root = me.Expression;
        while (root is MemberExpression inner)
            root = inner.Expression;

        return root as ParameterExpression;
    }

    private static string QualifiedTable(object vault)
    {
        dynamic v = vault;
        return $"\"{v.Keyspace.Name}\".\"{v.VaultDocument.Name}\"";
    }

    private static VaultDocument GetDocument(object vault)
    {
        dynamic v = vault;
        return v.VaultDocument;
    }

    private static string Operator(ExpressionType t) => t switch
    {
        ExpressionType.Equal => "=",
        ExpressionType.NotEqual => "!=",
        ExpressionType.GreaterThan => ">",
        ExpressionType.GreaterThanOrEqual => ">=",
        ExpressionType.LessThan => "<",
        ExpressionType.LessThanOrEqual => "<=",
        ExpressionType.AndAlso => "AND",
        ExpressionType.OrElse => "OR",
        _ => throw new NotSupportedException($"Unsupported operator: {t}")
    };

    private static string FormatValue(object? value) => value switch
    {
        null => "NULL",
        string s => $"'{s.Replace("'", "''")}'",
        bool b => b ? "TRUE" : "FALSE",
        DateTime dt => $"'{dt:yyyy-MM-dd HH:mm:ss}'",
        Enum e => Convert.ToInt64(e).ToString(),
        _ => value!.ToString()!
    };

    private static Expression StripConvert(Expression expr)
    {
        while (expr is UnaryExpression u &&
               (u.NodeType == ExpressionType.Convert || u.NodeType == ExpressionType.ConvertChecked))
            expr = u.Operand;

        return expr;
    }

    // ---------------- Projection translation (N params) ----------------

    private static string Select(LambdaExpression selector, IReadOnlyDictionary<ParameterExpression, object> paramMap)
    {
        var body = StripConvert(selector.Body);

        if (body is ParameterExpression pe)
        {
            if (!paramMap.TryGetValue(pe, out var vault))
                throw new NotSupportedException("Projection parameter must be one of the query parameters.");

            // Postgres supports: "schema"."table".*
            return $"{QualifiedTable(vault)}.*";
        }

        // anonymous type: new { A = x.Prop, B = y.Prop2 }
        if (body is NewExpression ne && ne.Members is not null)
        {
            var cols = new List<string>(ne.Arguments.Count);
            for (int i = 0; i < ne.Arguments.Count; i++)
            {
                var alias = ne.Members[i].Name;
                var sqlExpr = SelectValue(ne.Arguments[i], paramMap);
                cols.Add($"{sqlExpr} AS \"{alias}\"");
            }
            return string.Join(", ", cols);
        }

        // DTO init: new T { Prop = x.Prop, Prop2 = y.Prop2 }
        if (body is MemberInitExpression mie)
        {
            var cols = new List<string>(mie.Bindings.Count);
            foreach (var b in mie.Bindings)
            {
                if (b is not MemberAssignment ma)
                    throw new NotSupportedException("Only member assignments supported in projections.");

                var alias = ma.Member.Name;
                var sqlExpr = SelectValue(ma.Expression, paramMap);
                cols.Add($"{sqlExpr} AS \"{alias}\"");
            }
            return string.Join(", ", cols);
        }

        throw new NotSupportedException("Projection must be 'new { ... }' or 'new T { ... }' or a direct parameter (e.g. (a,b)=>a).");
    }

    private static string SelectValue(Expression expr, IReadOnlyDictionary<ParameterExpression, object> paramMap)
    {
        expr = StripConvert(expr);

        // direct member => column
        if (expr is MemberExpression me)
        {
            var rootParam = GetRootParameter(me);
            if (rootParam is null || !paramMap.TryGetValue(rootParam, out var vault))
                throw new NotSupportedException("Projection member must be rooted in a query parameter.");

            return Resolve(me, vault);
        }

        // x ?? y
        if (expr is BinaryExpression be && be.NodeType == ExpressionType.Coalesce)
        {
            var leftSql = SelectValue(be.Left, paramMap);

            var rhs = StripConvert(be.Right);

            // x ?? null => x
            if (rhs is ConstantExpression ce && ce.Value is null)
                return leftSql;

            // COALESCE(x, <literal>)
            if (rhs is ConstantExpression ce2)
                return $"COALESCE({leftSql}, {FormatValue(ce2.Value)})";

            // COALESCE(x, otherColumn)
            if (rhs is MemberExpression me2)
            {
                var rightSql = SelectValue(me2, paramMap);
                return $"COALESCE({leftSql}, {rightSql})";
            }

            throw new NotSupportedException("Unsupported coalesce RHS in projection.");
        }

        // constants
        if (expr is ConstantExpression c)
            return FormatValue(c.Value);

        throw new NotSupportedException($"Unsupported projection expression: {expr.NodeType}");
    }
}
