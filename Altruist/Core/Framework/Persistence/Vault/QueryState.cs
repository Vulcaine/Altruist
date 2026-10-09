namespace Altruist.Persistence;

/// <summary>Clause buckets of a <see cref="QueryState"/>.</summary>
public enum QueryPosition
{
    /// <summary>Projection list items.</summary>
    SELECT,
    /// <summary>Source table (currently unused by the SQL vaults, which use the document's table).</summary>
    FROM,
    /// <summary>Filter fragments, AND-combined.</summary>
    WHERE,
    /// <summary>Sort keys.</summary>
    ORDER_BY,
    /// <summary>LIMIT clause.</summary>
    LIMIT,
    /// <summary>OFFSET clause.</summary>
    OFFSET,
    /// <summary>Reserved for UPDATE statements.</summary>
    UPDATE,
    /// <summary>Reserved for UPDATE SET assignments.</summary>
    SET
}

/// <summary>
/// Immutable per-chain query state.
/// Each fluent call creates a new state with one extra piece added.
/// </summary>
/// <remarks>
/// Used by <see cref="SqlVault{TVaultModel}"/> and <see cref="SqlHistoricalVault{TVaultModel}"/> providers; application
/// code does not build it. Each bucket is a set of SQL fragments: duplicates collapse, and a second fragment in the
/// LIMIT or OFFSET bucket is appended (not replaced). Do not mutate the exposed collections; they are shared between
/// derived states.
/// </remarks>
public sealed class QueryState
{
    /// <summary>SQL fragments per clause.</summary>
    public readonly Dictionary<QueryPosition, HashSet<string>> Parts;
    /// <summary>Bind parameters per clause (populated only when a fragment is added with a parameter; the SQL vaults inline values instead).</summary>
    public readonly Dictionary<QueryPosition, List<object?>> Parameters;

    /// <summary>Creates an empty state.</summary>
    public QueryState()
    {
        Parts = new Dictionary<QueryPosition, HashSet<string>>
        {
            { QueryPosition.SELECT,   new HashSet<string>(StringComparer.Ordinal) },
            { QueryPosition.FROM,     new HashSet<string>(StringComparer.Ordinal) },
            { QueryPosition.WHERE,    new HashSet<string>(StringComparer.Ordinal) },
            { QueryPosition.ORDER_BY, new HashSet<string>(StringComparer.Ordinal) },
            { QueryPosition.LIMIT,    new HashSet<string>(StringComparer.Ordinal) },
            { QueryPosition.OFFSET,   new HashSet<string>(StringComparer.Ordinal) },
            { QueryPosition.UPDATE,   new HashSet<string>(StringComparer.Ordinal) },
            { QueryPosition.SET,      new HashSet<string>(StringComparer.Ordinal) }
        };

        Parameters = new Dictionary<QueryPosition, List<object?>>
        {
            { QueryPosition.SELECT,   new List<object?>() },
            { QueryPosition.FROM,     new List<object?>() },
            { QueryPosition.WHERE,    new List<object?>() },
            { QueryPosition.ORDER_BY, new List<object?>() },
            { QueryPosition.LIMIT,    new List<object?>() },
            { QueryPosition.OFFSET,   new List<object?>() },
            { QueryPosition.UPDATE,   new List<object?>() },
            { QueryPosition.SET,      new List<object?>() }
        };
    }

    private QueryState(
        Dictionary<QueryPosition, HashSet<string>> parts,
        Dictionary<QueryPosition, List<object?>> parameters)
    {
        Parts = parts;
        Parameters = parameters;
    }

    /// <summary>Returns a new state with <paramref name="part"/> added to the <paramref name="pos"/> bucket; this state is unchanged.</summary>
    /// <param name="pos">Clause bucket.</param>
    /// <param name="part">SQL fragment.</param>
    /// <param name="parameter">Optional bind parameter recorded for the bucket (ignored when null).</param>
    /// <returns>The new state.</returns>
    public QueryState With(QueryPosition pos, string part, object? parameter = null)
    {
        // clone shallow; copy only the mutated bucket
        var newParts = new Dictionary<QueryPosition, HashSet<string>>(Parts.Count);
        foreach (var kv in Parts)
        {
            if (kv.Key == pos)
            {
                var copy = new HashSet<string>(kv.Value, StringComparer.Ordinal);
                copy.Add(part);
                newParts[kv.Key] = copy;
            }
            else
            {
                newParts[kv.Key] = kv.Value;
            }
        }

        var newParams = new Dictionary<QueryPosition, List<object?>>(Parameters.Count);
        foreach (var kv in Parameters)
        {
            if (kv.Key == pos && parameter is not null)
            {
                var copy = new List<object?>(kv.Value);
                copy.Add(parameter);
                newParams[kv.Key] = copy;
            }
            else
            {
                newParams[kv.Key] = kv.Value;
            }
        }

        return new QueryState(newParts, newParams);
    }

    /// <summary>Whether the bucket has at least one fragment.</summary>
    /// <param name="pos">Clause bucket.</param>
    /// <returns>True when non-empty.</returns>
    public bool HasAny(QueryPosition pos) => Parts[pos].Count > 0;

    /// <summary>Returns this state if it already has a SELECT list, otherwise a new state selecting every mapped column as <c>"column" AS "Property"</c>.</summary>
    /// <param name="doc">Table metadata supplying the column map.</param>
    /// <returns>A state with a projection.</returns>
    public QueryState EnsureProjectionSelected(VaultDocument doc)
    {
        if (HasAny(QueryPosition.SELECT))
            return this;

        var projection = string.Join(", ",
            doc.Columns.Select(kvp => $"{QuoteIdent(kvp.Value)} AS {QuoteIdent(kvp.Key)}"));

        return With(QueryPosition.SELECT, projection);
    }

    private static string QuoteIdent(string ident) => $"\"{ident.Replace("\"", "\"\"")}\"";
}
