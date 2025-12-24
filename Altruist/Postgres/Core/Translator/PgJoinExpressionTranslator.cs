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

        var value = Expression.Lambda(expr).Compile().DynamicInvoke();
        return FormatValue(value);
    }

    // ---------------- COLUMN ----------------

    public static string Column(LambdaExpression expr, object vault)
    {
        if (expr.Body is not MemberExpression me)
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
        _ => throw new NotSupportedException()
    };

    private static string FormatValue(object? value) => value switch
    {
        null => "NULL",
        string s => $"'{s.Replace("'", "''")}'",
        bool b => b ? "TRUE" : "FALSE",
        DateTime dt => $"'{dt:yyyy-MM-dd HH:mm:ss}'",
        _ => value!.ToString()!
    };

    private static string Select(LambdaExpression selector, object left, object right)
    {
        if (selector.Body is not NewExpression ne)
            throw new NotSupportedException("Projection must be new { ... }");

        var cols = new List<string>();

        for (int i = 0; i < ne.Arguments.Count; i++)
        {
            var arg = ne.Arguments[i];
            var alias = ne.Members![i].Name;

            if (arg is MemberExpression me)
            {
                var source = IsRooted(me, selector.Parameters[0]) ? left : right;
                cols.Add($"{Resolve(me, source)} AS \"{alias}\"");
            }
            else
            {
                throw new NotSupportedException("Only member projections supported.");
            }
        }

        return string.Join(", ", cols);
    }
}
