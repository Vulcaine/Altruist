/*
Copyright 2025 Aron Gere
Licensed under the Apache License, Version 2.0
*/

using System.Linq.Expressions;

using Microsoft.EntityFrameworkCore.Query;

namespace Altruist.Persistence.Postgres;

/// <summary>
/// Centralized PostgreSQL query translation logic for single-table vaults
/// (<see cref="PgVault{TVaultModel}"/>, <see cref="PgHistoricalVault{TVaultModel}"/>).
/// Converts LINQ expressions + metadata into SQL fragments with values inlined as escaped literals.
/// </summary>
/// <remarks>
/// Supported shapes are listed on <see cref="PgVault{TVaultModel}"/>; every other shape throws
/// <see cref="NotSupportedException"/>. Joined queries use <see cref="Querying.PgJoinExpressionTranslator"/> and prefab
/// filters use <see cref="PgPrefabWhereTranslator"/> instead.
/// </remarks>
internal static class PgQueryTranslator
{
    // -------------------- WHERE --------------------

    /// <summary>
    /// Translates a predicate into a WHERE fragment: property-vs-value comparisons combined with
    /// <c>&amp;&amp;</c>/<c>||</c> (each combination parenthesised).
    /// </summary>
    /// <exception cref="NotSupportedException">Any other node type, or a comparison with no model property side.</exception>
    public static string Where<T>(
        Expression<Func<T, bool>> predicate,
        VaultDocument document)
        where T : class
    {
        return VisitWhere(predicate.Body, predicate.Parameters[0], document);
    }

    private static string VisitWhere(
        Expression expr,
        ParameterExpression root,
        VaultDocument doc)
    {
        if (expr is UnaryExpression ue &&
            ue.NodeType is ExpressionType.Convert or ExpressionType.ConvertChecked)
            return VisitWhere(ue.Operand, root, doc);

        if (expr is BinaryExpression be &&
            (be.NodeType == ExpressionType.AndAlso || be.NodeType == ExpressionType.OrElse))
        {
            var op = Operator(be.NodeType);
            return $"({VisitWhere(be.Left, root, doc)} {op} {VisitWhere(be.Right, root, doc)})";
        }

        if (expr is BinaryExpression cmp && IsComparison(cmp.NodeType))
        {
            if (!TryColumn(cmp.Left, root, doc, out var col))
            {
                if (!TryColumn(cmp.Right, root, doc, out col))
                    throw new NotSupportedException("WHERE must compare against a model property.");

                var flipped = Flip(cmp.NodeType);
                var val = ExpressionUtils.Evaluate(cmp.Left);
                return Compare(col, flipped, val);
            }

            var value = ExpressionUtils.Evaluate(cmp.Right);
            return Compare(col, cmp.NodeType, value);
        }

        throw new NotSupportedException("Unsupported WHERE expression.");
    }

    // -------------------- ORDER BY --------------------

    /// <summary>Translates <c>x =&gt; x.Prop</c> into a quoted column name (no direction; callers append <c>DESC</c>).</summary>
    /// <exception cref="NotSupportedException">The body is not a plain member access.</exception>
    public static string OrderBy<T, TKey>(
        Expression<Func<T, TKey>> selector,
        VaultDocument doc)
        where T : class
    {
        if (selector.Body is not MemberExpression me)
            throw new NotSupportedException("ORDER BY must be a property.");

        var col = ResolveColumn(me.Member.Name, doc);
        return Quote(col);
    }

    // -------------------- SELECT --------------------

    /// <summary>
    /// Translates <c>x =&gt; new { A = ..., B = ... }</c> into <c>"col" AS "Member"</c> items, one per constructor
    /// member. Only member names matter; the value expressions are ignored.
    /// </summary>
    /// <exception cref="NotSupportedException">The body is not a <see cref="NewExpression"/> with members (enumerated lazily).</exception>
    public static IEnumerable<string> Select<T, TResult>(
        Expression<Func<T, TResult>> selector,
        VaultDocument doc)
        where T : class
    {
        if (selector.Body is not NewExpression ne || ne.Members is null)
            throw new NotSupportedException("SELECT must be: x => new { ... }");

        for (int i = 0; i < ne.Members.Count; i++)
        {
            var name = ne.Members[i].Name;
            var col = ResolveColumn(name, doc);
            yield return $"{Quote(col)} AS {Quote(name)}";
        }
    }

    // -------------------- UPDATE (SetPropertyCalls) --------------------

    /// <summary>
    /// Builds an <c>UPDATE ... SET ... [WHERE ...]</c> from a member-init expression. Currently unused by the
    /// vaults (<c>UpdateAsync</c> loads, modifies and re-saves rows instead).
    /// </summary>
    /// <exception cref="NotSupportedException">The body is not a <see cref="MemberInitExpression"/>.</exception>
    public static string BuildUpdate<T>(
        Expression<Func<SetPropertyCalls<T>, SetPropertyCalls<T>>> setExpression,
        QueryState state,
        VaultDocument doc,
        string qualifiedTable)
        where T : class
    {
        var updates = new List<string>();

        if (setExpression.Body is not MemberInitExpression mi)
            throw new NotSupportedException("UPDATE must use object initializer syntax.");

        foreach (var binding in mi.Bindings.OfType<MemberAssignment>())
        {
            var column = ResolveColumn(binding.Member.Name, doc);
            var value = ExpressionUtils.Evaluate(binding.Expression);

            updates.Add($"{Quote(column)} = {SqlValue(value)}");
        }

        var where = string.Join(" AND ", state.Parts[QueryPosition.WHERE]);

        var sql = $"UPDATE {qualifiedTable} SET {string.Join(", ", updates)}";
        if (!string.IsNullOrEmpty(where))
            sql += $" WHERE {where}";

        return sql;
    }

    // -------------------- UPDATE (dictionary-based) --------------------

    /// <summary>
    /// Builds an <c>UPDATE</c> keyed by primary-key values (null key values become <c>IS NULL</c>). Currently unused.
    /// </summary>
    public static string BuildUpdate(
        IReadOnlyDictionary<string, object?> primaryKey,
        IReadOnlyDictionary<string, object?> changes,
        VaultDocument doc,
        string qualifiedTable)
    {
        var sets = changes.Select(kv =>
        {
            var col = ResolveColumn(kv.Key, doc);
            return $"{Quote(col)} = {SqlValue(kv.Value)}";
        });

        var wheres = primaryKey.Select(kv =>
        {
            var col = ResolveColumn(kv.Key, doc);
            return kv.Value is null
                ? $"{Quote(col)} IS NULL"
                : $"{Quote(col)} = {SqlValue(kv.Value)}";
        });

        return
            $"UPDATE {qualifiedTable} " +
            $"SET {string.Join(", ", sets)} " +
            $"WHERE {string.Join(" AND ", wheres)}";
    }

    // -------------------- Helpers --------------------

    private static bool TryColumn(
        Expression expr,
        ParameterExpression root,
        VaultDocument doc,
        out string column)
    {
        while (expr is UnaryExpression ue)
            expr = ue.Operand;

        if (expr is MemberExpression me && IsRooted(me, root))
        {
            column = Quote(ResolveColumn(me.Member.Name, doc));
            return true;
        }

        column = "";
        return false;
    }

    private static bool IsRooted(MemberExpression me, ParameterExpression root)
    {
        Expression? e = me.Expression;
        while (e is MemberExpression inner)
            e = inner.Expression;
        return e == root;
    }

    private static string Compare(string col, ExpressionType op, object? value)
    {
        if (value is null)
            return op == ExpressionType.Equal
                ? $"{col} IS NULL"
                : $"{col} IS NOT NULL";

        return $"{col} {Operator(op)} {SqlValue(value)}";
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
        ExpressionType.NotEqual => "!=",
        ExpressionType.GreaterThan => ">",
        ExpressionType.GreaterThanOrEqual => ">=",
        ExpressionType.LessThan => "<",
        ExpressionType.LessThanOrEqual => "<=",
        ExpressionType.AndAlso => "AND",
        ExpressionType.OrElse => "OR",
        _ => throw new NotSupportedException()
    };

    private static string ResolveColumn(string prop, VaultDocument doc) =>
        doc.Columns.TryGetValue(prop, out var c) ? c : VaultDocument.ToCamelCase(prop);

    private static string Quote(string s) => $"\"{s.Replace("\"", "\"\"")}\"";

    // Literals are inlined (the vault pipeline has no parameters), so every format must be
    // culture-invariant and strings must not be able to terminate the literal.
    private static string SqlValue(object? value) => value switch
    {
        null => "NULL",
        string s => StringLiteral(s),
        char c => StringLiteral(c.ToString()),
        bool b => b ? "TRUE" : "FALSE",
        DateTime dt => $"'{dt.ToString("yyyy-MM-dd HH:mm:ss.ffffff", System.Globalization.CultureInfo.InvariantCulture)}'",
        DateTimeOffset dto => $"'{dto.ToUniversalTime().ToString("yyyy-MM-dd HH:mm:ss.ffffff", System.Globalization.CultureInfo.InvariantCulture)}+00'",
        Guid g => $"'{g:D}'",
        Enum e => Convert.ToInt64(e, System.Globalization.CultureInfo.InvariantCulture).ToString(System.Globalization.CultureInfo.InvariantCulture),
        float f => f.ToString("R", System.Globalization.CultureInfo.InvariantCulture),
        double d => d.ToString("R", System.Globalization.CultureInfo.InvariantCulture),
        decimal m => m.ToString(System.Globalization.CultureInfo.InvariantCulture),
        IFormattable fm => fm.ToString(null, System.Globalization.CultureInfo.InvariantCulture),
        _ => StringLiteral(value.ToString() ?? "")
    };

    /// <summary>
    /// Renders <paramref name="s"/> as a Postgres escape-string literal (<c>E'...'</c>) with backslashes and quotes
    /// escaped, safe regardless of <c>standard_conforming_strings</c>.
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="s"/> contains a NUL character.</exception>
    internal static string StringLiteral(string s)
    {
        if (s.Contains('\0'))
            throw new ArgumentException("String literals may not contain NUL characters.");
        // Escape-string syntax with escaped backslashes and doubled quotes is exact whatever
        // standard_conforming_strings is set to; a plain '...' literal would let a trailing
        // backslash swallow the closing quote on a server running with it off.
        return $"E'{s.Replace("\\", "\\\\").Replace("'", "''")}'";
    }
}
