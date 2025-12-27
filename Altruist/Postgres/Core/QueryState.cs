namespace Altruist.Persistence.Postgres;

public enum QueryPosition
{
    SELECT,
    FROM,
    WHERE,
    ORDER_BY,
    LIMIT,
    OFFSET,
    UPDATE,
    SET
}

/// <summary>
/// Immutable per-chain query state.
/// Each fluent call creates a new state with one extra piece added.
/// </summary>
public sealed class QueryState
{
    public readonly Dictionary<QueryPosition, HashSet<string>> Parts;
    public readonly Dictionary<QueryPosition, List<object?>> Parameters;

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

    public bool HasAny(QueryPosition pos) => Parts[pos].Count > 0;

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
