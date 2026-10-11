/*
Copyright 2025 Aron Gere
Licensed under the Apache License, Version 2.0
*/

using System.Linq.Expressions;

namespace Altruist.Persistence.Postgres;

/// <summary>
/// Centralized PostgreSQL query translation logic for single-table vaults
/// (<see cref="PgVault{TVaultModel}"/>, <see cref="PgHistoricalVault{TVaultModel}"/>).
/// Converts LINQ expressions + metadata into SQL fragments with values inlined as escaped literals.
/// </summary>
/// <remarks>
/// Predicates follow the rules of <see cref="PgPredicateTranslator"/>; every other shape throws
/// <see cref="NotSupportedException"/>. Joined queries use <see cref="Querying.PgJoinChain"/> and prefab
/// filters use <see cref="PgPrefabWhereTranslator"/> instead.
/// </remarks>
internal static class PgQueryTranslator
{
    /// <summary>Translates a predicate into a WHERE fragment (unqualified, quoted column names).</summary>
    /// <exception cref="NotSupportedException">An unsupported shape, or a property that is not a mapped column.</exception>
    public static string Where<T>(Expression<Func<T, bool>> predicate, VaultDocument document)
        where T : class
        => PgPredicateTranslator.Translate(predicate.Body, ColumnsOf(predicate.Parameters[0], document));

    /// <summary>Translates <c>x =&gt; x.Prop</c> into a quoted column name (no direction; callers append <c>DESC</c>).</summary>
    /// <exception cref="NotSupportedException">The body is not a mapped property of the parameter.</exception>
    public static string OrderBy<T, TKey>(Expression<Func<T, TKey>> selector, VaultDocument doc)
        where T : class
        => PgPredicateTranslator.Column(selector.Body, ColumnsOf(selector.Parameters[0], doc));

    /// <summary>
    /// Translates a projection into <c>"col" AS "Member"</c> items: <c>x =&gt; new T { A = x.B, ... }</c> (each bound
    /// member reads the column of the source property it is assigned from) or a constructor call with named members
    /// (<c>new { A = x.B }</c>).
    /// </summary>
    /// <exception cref="NotSupportedException">Another body shape, or a bound value that is not a mapped property.</exception>
    public static IReadOnlyList<string> Select<T, TResult>(Expression<Func<T, TResult>> selector, VaultDocument doc)
        where T : class
    {
        var columns = ColumnsOf(selector.Parameters[0], doc);

        string Item(Expression value, string alias)
            => $"{PgPredicateTranslator.Column(value, columns)} AS {PgLiterals.Ident(alias)}";

        return selector.Body switch
        {
            MemberInitExpression init when init.NewExpression.Arguments.Count == 0 => init.Bindings
                .Select(b => b is MemberAssignment ma
                    ? Item(ma.Expression, ma.Member.Name)
                    : throw new NotSupportedException("SELECT supports only member assignments: x => new T { A = x.B }."))
                .ToArray(),
            NewExpression ne when ne.Members is not null => ne.Arguments
                .Select((arg, i) => Item(arg, ne.Members[i].Name))
                .ToArray(),
            _ => throw new NotSupportedException("SELECT must be: x => new T { A = x.A, ... }")
        };
    }

    /// <summary>The physical column of a mapped property.</summary>
    /// <exception cref="NotSupportedException">The property is not a <c>[VaultColumn]</c> of the model.</exception>
    public static string ColumnOf(VaultDocument doc, string property)
        => doc.Columns.TryGetValue(property, out var column)
            ? column
            : throw new NotSupportedException(
                $"'{property}' is not a mapped [VaultColumn] of {doc.Type.Name}; only mapped properties can be queried.");

    private static PgColumnResolver ColumnsOf(ParameterExpression root, VaultDocument doc)
        => (parameter, member) => parameter == root ? PgLiterals.Ident(ColumnOf(doc, member.Name)) : null;

    /// <summary>Renders <paramref name="s"/> as an escape-string literal; see <see cref="PgLiterals.StringLiteral"/>.</summary>
    internal static string StringLiteral(string s) => PgLiterals.StringLiteral(s);
}
