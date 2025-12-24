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

    // ---------------- SELECT ----------------

    public static string BuildSelect(
        LambdaExpression? projection,
        object left,
        object right,
        IReadOnlyList<string> joins,
        IReadOnlyList<string> wheres)
    {
        var select =
            projection is null
                ? "*"
                : Select(projection, left, right);

        var sql = $"SELECT {select} FROM {QualifiedTable(left)}";

        if (joins.Count > 0)
            sql += " " + string.Join(" ", joins);

        if (wheres.Count > 0)
            sql += " WHERE " + string.Join(" AND ", wheres);

        return sql;
    }

    // ---------------- WHERE ----------------

    public static string Translate<TLeft, TRight>(
        Expression<Func<TLeft, TRight, bool>> predicate,
        PgVault<TLeft> left,
        PgVault<TRight> right)
        where TLeft : class, IVaultModel
        where TRight : class, IVaultModel
    {
        return Visit(predicate.Body,
            predicate.Parameters[0],
            predicate.Parameters[1],
            left,
            right);
    }

    private static string Visit(
        Expression expr,
        ParameterExpression leftParam,
        ParameterExpression rightParam,
        object left,
        object right)
    {
        expr = StripConvert(expr);

        if (expr is BinaryExpression be)
        {
            var op = Operator(be.NodeType);
            return $"({Visit(be.Left, leftParam, rightParam, left, right)} {op} {Visit(be.Right, leftParam, rightParam, left, right)})";
        }

        if (expr is MemberExpression me)
        {
            if (IsRooted(me, leftParam))
                return Resolve(me, left);
            if (IsRooted(me, rightParam))
                return Resolve(me, right);
        }

        // NOTE: This is still "dynamic evaluation" of constants in WHERE.
        // If you want 0 compilation/reflection, replace this with a safe constant extractor.
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
            : Document.ToCamelCase(me.Member.Name);

        return $"{QualifiedTable(vault)}.\"{col}\"";
    }

    // ---------------- HELPERS ----------------

    private static bool IsRooted(MemberExpression me, ParameterExpression param)
    {
        Expression? root = me.Expression;
        while (root is MemberExpression inner)
            root = inner.Expression;
        return root == param;
    }

    private static string QualifiedTable(object vault)
    {
        dynamic v = vault;
        return $"\"{v.Keyspace.Name}\".\"{v.VaultDocument.Name}\"";
    }

    private static Document GetDocument(object vault)
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
        Enum e => Convert.ToInt64(e).ToString(), // safer for SQL than enum name
        _ => value!.ToString()!
    };

    private static Expression StripConvert(Expression expr)
    {
        while (expr is UnaryExpression u &&
               (u.NodeType == ExpressionType.Convert || u.NodeType == ExpressionType.ConvertChecked))
            expr = u.Operand;

        return expr;
    }

    // ---------------- Projection translation ----------------

    private static string Select(LambdaExpression selector, object left, object right)
    {
        var body = StripConvert(selector.Body);

        // anonymous type: new { A = c.X, B = e.Y }
        if (body is NewExpression ne && ne.Members is not null)
        {
            var cols = new List<string>(ne.Arguments.Count);

            for (int i = 0; i < ne.Arguments.Count; i++)
            {
                var alias = ne.Members[i].Name;
                var sqlExpr = SelectValue(ne.Arguments[i], selector.Parameters[0], selector.Parameters[1], left, right);
                cols.Add($"{sqlExpr} AS \"{alias}\"");
            }

            return string.Join(", ", cols);
        }

        // DTO initializer: new T { Prop = c.X, Prop2 = e.Y }
        if (body is MemberInitExpression mie)
        {
            var cols = new List<string>(mie.Bindings.Count);

            foreach (var b in mie.Bindings)
            {
                if (b is not MemberAssignment ma)
                    throw new NotSupportedException("Only member assignments supported in projections.");

                var alias = ma.Member.Name;
                var sqlExpr = SelectValue(ma.Expression, selector.Parameters[0], selector.Parameters[1], left, right);
                cols.Add($"{sqlExpr} AS \"{alias}\"");
            }

            return string.Join(", ", cols);
        }

        throw new NotSupportedException("Projection must be 'new { ... }' or 'new T { ... }'.");
    }

    private static string SelectValue(
        Expression expr,
        ParameterExpression leftParam,
        ParameterExpression rightParam,
        object left,
        object right)
    {
        expr = StripConvert(expr);

        // direct column: c.Name / e.ItemInstanceId etc.
        if (expr is MemberExpression me)
        {
            var source =
                IsRooted(me, leftParam) ? left :
                IsRooted(me, rightParam) ? right :
                throw new NotSupportedException("Projection member must be rooted in left or right parameter.");

            return Resolve(me, source);
        }

        // x ?? y
        if (expr is BinaryExpression be && be.NodeType == ExpressionType.Coalesce)
        {
            var leftSql = SelectValue(be.Left, leftParam, rightParam, left, right);

            var rhs = StripConvert(be.Right);

            // x ?? null  => x (same semantics for ref types)
            if (rhs is ConstantExpression ce && ce.Value is null)
                return leftSql;

            // COALESCE(x, <literal>)
            if (rhs is ConstantExpression ce2)
                return $"COALESCE({leftSql}, {FormatValue(ce2.Value)})";

            // COALESCE(x, otherColumn)
            if (rhs is MemberExpression me2)
            {
                var rightSql = SelectValue(me2, leftParam, rightParam, left, right);
                return $"COALESCE({leftSql}, {rightSql})";
            }

            throw new NotSupportedException("Unsupported coalesce RHS in projection.");
        }

        // constants (rare, but allow)
        if (expr is ConstantExpression c)
            return FormatValue(c.Value);

        throw new NotSupportedException($"Unsupported projection expression: {expr.NodeType}");
    }
}
