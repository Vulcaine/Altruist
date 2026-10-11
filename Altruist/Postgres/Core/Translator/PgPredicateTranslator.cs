/*
Copyright 2025 Aron Gere
Licensed under the Apache License, Version 2.0
*/

using System.Linq.Expressions;
using System.Reflection;

namespace Altruist.Persistence.Postgres;

/// <summary>
/// Resolves a mapped member of one lambda parameter to its SQL column expression (e.g. <c>"rank"</c> or
/// <c>"t1"."rank"</c>). Returns <c>null</c> when <paramref name="parameter"/> is not one of the query's parameters.
/// </summary>
/// <param name="parameter">The lambda parameter the member is read from.</param>
/// <param name="member">The member read from it.</param>
/// <returns>The column SQL, or <c>null</c>.</returns>
internal delegate string? PgColumnResolver(ParameterExpression parameter, MemberInfo member);

/// <summary>
/// Shared WHERE translation of the single-table vaults and the join layer. Leaves are columns (members of a query
/// parameter) or values (anything else, evaluated client-side and inlined as a literal).
/// </summary>
/// <remarks>
/// <para>
/// Supported: <c>&amp;&amp;</c>, <c>||</c> and the comparison operators between a column and a value (either side)
/// or between two columns. Comparisons follow C# semantics, including for <c>null</c>: <c>== null</c> is
/// <c>IS NULL</c>, <c>!= null</c> is <c>IS NOT NULL</c>, an ordering comparison with <c>null</c> is never true
/// (<c>FALSE</c>), <c>!=</c> is <c>IS DISTINCT FROM</c> (a NULL column is "not equal" to a value, as in C#), and
/// column-to-column <c>==</c> is <c>IS NOT DISTINCT FROM</c> (two NULLs are equal).
/// </para>
/// <para>
/// A column is a direct member of a query parameter (<c>x.Prop</c>), or <c>x.Prop.Value</c> on a nullable property.
/// Deeper member paths (<c>x.A.B</c>) are not columns and throw <see cref="NotSupportedException"/>, as does any other
/// node type (method calls, <c>!</c>, bare bool members, arithmetic on columns).
/// </para>
/// </remarks>
internal static class PgPredicateTranslator
{
    private readonly record struct Operand(string? ColumnSql, object? Value)
    {
        public bool IsColumn => ColumnSql is not null;
    }

    /// <summary>Translates a predicate body to a SQL boolean expression.</summary>
    /// <param name="body">The predicate body.</param>
    /// <param name="column">Column resolver for the query's parameters.</param>
    /// <returns>SQL without the <c>WHERE</c> keyword.</returns>
    /// <exception cref="NotSupportedException">An unsupported node or member path.</exception>
    public static string Translate(Expression body, PgColumnResolver column)
    {
        var expr = StripConvert(body);

        if (expr is BinaryExpression logical &&
            logical.NodeType is ExpressionType.AndAlso or ExpressionType.OrElse)
        {
            var op = logical.NodeType == ExpressionType.AndAlso ? "AND" : "OR";
            return $"({Translate(logical.Left, column)} {op} {Translate(logical.Right, column)})";
        }

        if (expr is BinaryExpression cmp && IsComparison(cmp.NodeType))
        {
            // A comparison of two client-side values is a constant.
            if (!ReferencesParameter(cmp))
                return (bool)Evaluate(cmp)! ? "TRUE" : "FALSE";
            return Compare(Resolve(cmp.Left, column), cmp.NodeType, Resolve(cmp.Right, column));
        }

        throw new NotSupportedException(
            $"Unsupported WHERE expression '{body}'. Use comparisons between mapped properties and values, combined with && and ||.");
    }

    /// <summary>
    /// Resolves an expression that must be a column (order keys, join keys, projected members).
    /// </summary>
    /// <param name="expr">The expression (conversions are stripped).</param>
    /// <param name="column">Column resolver for the query's parameters.</param>
    /// <returns>The column SQL.</returns>
    /// <exception cref="NotSupportedException">The expression is not a column of a query parameter.</exception>
    public static string Column(Expression expr, PgColumnResolver column)
    {
        var operand = Resolve(expr, column);
        return operand.ColumnSql
            ?? throw new NotSupportedException($"'{expr}' must be a mapped property of a query parameter, e.g. x => x.Prop.");
    }

    private static Operand Resolve(Expression expr, PgColumnResolver column)
    {
        expr = StripConvert(expr);

        if (TryRootedMember(expr, out var parameter, out var member))
        {
            var sql = column(parameter, member)
                ?? throw new NotSupportedException($"'{expr}' reads a parameter that is not part of this query.");
            return new Operand(sql, null);
        }

        if (ReferencesParameter(expr))
            throw new NotSupportedException(
                $"'{expr}' is not supported: only direct members of the query parameter (x.Prop, or x.Prop.Value on a nullable) can be compared.");

        return new Operand(null, Evaluate(expr));
    }

    private static bool TryRootedMember(Expression expr, out ParameterExpression parameter, out MemberInfo member)
    {
        parameter = null!;
        member = null!;

        if (expr is not MemberExpression me)
            return false;

        if (me.Expression is ParameterExpression direct)
        {
            (parameter, member) = (direct, me.Member);
            return true;
        }

        // x.Prop.Value on a Nullable<T> property is the column itself.
        if (me.Member.Name == nameof(Nullable<int>.Value) &&
            me.Expression is MemberExpression { Expression: ParameterExpression owner } inner &&
            Nullable.GetUnderlyingType(inner.Type) is not null)
        {
            (parameter, member) = (owner, inner.Member);
            return true;
        }

        return false;
    }

    private static string Compare(Operand left, ExpressionType op, Operand right)
    {
        if (left.IsColumn && right.IsColumn)
        {
            return op switch
            {
                ExpressionType.Equal => $"{left.ColumnSql} IS NOT DISTINCT FROM {right.ColumnSql}",
                ExpressionType.NotEqual => $"{left.ColumnSql} IS DISTINCT FROM {right.ColumnSql}",
                _ => $"{left.ColumnSql} {Operator(op)} {right.ColumnSql}"
            };
        }

        var (col, value, effectiveOp) = left.IsColumn
            ? (left.ColumnSql!, right.Value, op)
            : (right.ColumnSql!, left.Value, Flip(op));

        if (value is null)
        {
            return effectiveOp switch
            {
                ExpressionType.Equal => $"{col} IS NULL",
                ExpressionType.NotEqual => $"{col} IS NOT NULL",
                // C# lifted comparisons with null are always false.
                _ => "FALSE"
            };
        }

        return effectiveOp == ExpressionType.NotEqual
            ? $"{col} IS DISTINCT FROM {PgLiterals.Format(value)}"
            : $"{col} {Operator(effectiveOp)} {PgLiterals.Format(value)}";
    }

    private static object? Evaluate(Expression expr)
        => expr is ConstantExpression c
            ? c.Value
            : Expression.Lambda<Func<object?>>(Expression.Convert(expr, typeof(object))).Compile().Invoke();

    private static bool ReferencesParameter(Expression expr)
    {
        var finder = new ParameterFinder();
        finder.Visit(expr);
        return finder.Found;
    }

    private sealed class ParameterFinder : ExpressionVisitor
    {
        public bool Found { get; private set; }

        protected override Expression VisitParameter(ParameterExpression node)
        {
            Found = true;
            return node;
        }

        protected override Expression VisitLambda<T>(Expression<T> node)
        {
            // Parameters of nested lambdas are their own, not the query's.
            Visit(node.Body);
            return node;
        }
    }

    private static Expression StripConvert(Expression expr)
    {
        while (expr is UnaryExpression u && u.NodeType is ExpressionType.Convert or ExpressionType.ConvertChecked)
            expr = u.Operand;
        return expr;
    }

    private static bool IsComparison(ExpressionType t) =>
        t is ExpressionType.Equal or ExpressionType.NotEqual
          or ExpressionType.GreaterThan or ExpressionType.GreaterThanOrEqual
          or ExpressionType.LessThan or ExpressionType.LessThanOrEqual;

    private static ExpressionType Flip(ExpressionType t) => t switch
    {
        ExpressionType.GreaterThan => ExpressionType.LessThan,
        ExpressionType.GreaterThanOrEqual => ExpressionType.LessThanOrEqual,
        ExpressionType.LessThan => ExpressionType.GreaterThan,
        ExpressionType.LessThanOrEqual => ExpressionType.GreaterThanOrEqual,
        _ => t
    };

    private static string Operator(ExpressionType t) => t switch
    {
        ExpressionType.Equal => "=",
        ExpressionType.NotEqual => "<>",
        ExpressionType.GreaterThan => ">",
        ExpressionType.GreaterThanOrEqual => ">=",
        ExpressionType.LessThan => "<",
        ExpressionType.LessThanOrEqual => "<=",
        _ => throw new NotSupportedException($"Unsupported operator: {t}")
    };
}
