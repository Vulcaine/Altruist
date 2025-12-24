/*
Copyright 2025 Aron Gere
Licensed under the Apache License, Version 2.0
*/

using System.Linq.Expressions;

using Microsoft.EntityFrameworkCore.Query;

namespace Altruist.Persistence.Postgres;

/// <summary>
/// Centralized PostgreSQL query translation logic.
/// Converts LINQ expressions + metadata into SQL fragments.
/// </summary>
internal static class PgQueryTranslator
{
    // -------------------- WHERE --------------------

    public static string Where<T>(
        Expression<Func<T, bool>> predicate,
        Document document)
        where T : class
    {
        return VisitWhere(predicate.Body, predicate.Parameters[0], document);
    }

    private static string VisitWhere(
        Expression expr,
        ParameterExpression root,
        Document doc)
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

    public static string OrderBy<T, TKey>(
        Expression<Func<T, TKey>> selector,
        Document doc)
        where T : class
    {
        if (selector.Body is not MemberExpression me)
            throw new NotSupportedException("ORDER BY must be a property.");

        var col = ResolveColumn(me.Member.Name, doc);
        return Quote(col);
    }

    // -------------------- SELECT --------------------

    public static IEnumerable<string> Select<T, TResult>(
        Expression<Func<T, TResult>> selector,
        Document doc)
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

    public static string BuildUpdate<T>(
        Expression<Func<SetPropertyCalls<T>, SetPropertyCalls<T>>> setExpression,
        QueryState state,
        Document doc,
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

    public static string BuildUpdate(
        IReadOnlyDictionary<string, object?> primaryKey,
        IReadOnlyDictionary<string, object?> changes,
        Document doc,
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
        Document doc,
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

    private static string ResolveColumn(string prop, Document doc) =>
        doc.Columns.TryGetValue(prop, out var c) ? c : Document.ToCamelCase(prop);

    private static string Quote(string s) => $"\"{s.Replace("\"", "\"\"")}\"";

    private static string SqlValue(object? value) => value switch
    {
        null => "NULL",
        string s => $"'{s.Replace("'", "''")}'",
        bool b => b ? "TRUE" : "FALSE",
        DateTime dt => $"'{dt:yyyy-MM-dd HH:mm:ss}'",
        Enum e => Convert.ToInt32(e).ToString(),
        _ => value.ToString()!
    };
}
