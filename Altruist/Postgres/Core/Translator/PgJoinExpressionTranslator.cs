using System.Linq.Expressions;

using Altruist.Querying;

namespace Altruist.Persistence.Postgres.Querying;

/// <summary>
/// Translator for multi-table queries (<see cref="PgVaultQuery"/> join chains). Vaults are passed as
/// <c>object</c> and read dynamically (<c>Keyspace</c>, <c>VaultDocument</c>); each lambda parameter is mapped to
/// the vault of its table, and columns are emitted fully qualified (<c>"schema"."table"."col"</c>).
/// </summary>
/// <remarks>
/// Unlike <see cref="PgQueryTranslator"/> the WHERE visitor accepts any binary tree of comparison and
/// <c>&amp;&amp;</c>/<c>||</c> nodes (column-to-column included); leaves that are not rooted in a query parameter are
/// compiled and evaluated client-side. Null values are emitted as <c>NULL</c> with the plain operator
/// (<c>= NULL</c>), not <c>IS NULL</c>.
/// </remarks>
internal static class PgJoinExpressionTranslator
{
    // ---------------- JOIN ----------------

    /// <summary>Typed wrapper over <see cref="BuildJoinDynamic"/>.</summary>
    public static string BuildJoin<TLeft, TRight>(
        PgVault<TLeft> left,
        PgVault<TRight> right,
        LambdaExpression leftKey,
        LambdaExpression rightKey,
        JoinType joinType)
        where TLeft : class, IVaultModel
        where TRight : class, IVaultModel
        => BuildJoinDynamic(left, right, leftKey, rightKey, joinType);

    /// <summary>
    /// Builds <c>&lt;JOIN KIND&gt; "schema"."right" ON left.col = right.col</c>.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">Unknown <paramref name="joinType"/>.</exception>
    /// <exception cref="NotSupportedException">A key selector is not a member access.</exception>
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

    /// <summary>
    /// Builds <c>SELECT ... FROM root [joins] [WHERE a AND b ...]</c>; a null <paramref name="projection"/> selects
    /// <c>*</c>. ORDER BY / LIMIT / OFFSET are appended by the caller.
    /// </summary>
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

    /// <summary>Translates a multi-parameter predicate into a parenthesised WHERE fragment.</summary>
    /// <exception cref="NotSupportedException">A binary operator other than comparisons, <c>&amp;&amp;</c> or <c>||</c>.</exception>
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

    /// <summary>Resolves a key selector (<c>x =&gt; x.Prop</c>, conversions stripped) to a fully qualified column.</summary>
    /// <exception cref="NotSupportedException">The body is not a member access.</exception>
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
        string s => PgQueryTranslator.StringLiteral(s),
        bool b => b ? "TRUE" : "FALSE",
        DateTime dt => $"'{dt.ToString("yyyy-MM-dd HH:mm:ss.ffffff", System.Globalization.CultureInfo.InvariantCulture)}'",
        Enum e => Convert.ToInt64(e, System.Globalization.CultureInfo.InvariantCulture).ToString(System.Globalization.CultureInfo.InvariantCulture),
        IFormattable f when value is sbyte or byte or short or ushort or int or uint or long or ulong or float or double or decimal
            => f.ToString(null, System.Globalization.CultureInfo.InvariantCulture),
        // Anything else (Guid, custom types) is quoted: ToString() output is never emitted raw.
        _ => PgQueryTranslator.StringLiteral(value!.ToString() ?? "")
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

        // (a,b,...) => a   ==> expand columns with aliases to CLR property names
        if (body is ParameterExpression pe)
        {
            if (!paramMap.TryGetValue(pe, out var vault))
                throw new NotSupportedException("Projection parameter must be one of the query parameters.");

            var doc = GetDocument(vault);

            static string QuoteIdent(string s) => $"\"{s.Replace("\"", "\"\"")}\"";

            // Emit: "schema"."table"."character-id" AS "CharacterId", ...
            var cols = new List<string>(doc.Columns.Count);
            foreach (var kvp in doc.Columns) // kvp.Key = CLR property name, kvp.Value = SQL column name
            {
                var propName = kvp.Key;
                var colName = kvp.Value;

                cols.Add($"{QualifiedTable(vault)}.{QuoteIdent(colName)} AS {QuoteIdent(propName)}");
            }

            return string.Join(", ", cols);
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

        throw new NotSupportedException(
            "Projection must be 'new { ... }' or 'new T { ... }' or a direct parameter (e.g. (a,b)=>a).");
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
