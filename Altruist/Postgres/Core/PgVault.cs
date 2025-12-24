/*
Copyright 2025 Aron Gere

Licensed under the Apache License, Version 2.0 (the "License");
you may not use this file except in compliance with the License.
You may obtain a copy of the License at

    http://www.apache.org/licenses/LICENSE-2.0
*/

using System.Collections.Concurrent;
using System.Linq.Expressions;
using System.Reflection;

using Microsoft.EntityFrameworkCore.Query;

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
/// </summary>
public sealed class QueryState
{
    public readonly Dictionary<QueryPosition, HashSet<string>> Parts;
    public readonly Dictionary<QueryPosition, List<object?>> Parameters;

    public QueryState()
    {
        Parts = Enum.GetValues<QueryPosition>()
            .ToDictionary(p => p, _ => new HashSet<string>(StringComparer.Ordinal));

        Parameters = Enum.GetValues<QueryPosition>()
            .ToDictionary(p => p, _ => new List<object?>());
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
        var newParts = Parts.ToDictionary(
            kv => kv.Key,
            kv => kv.Key == pos
                ? new HashSet<string>(kv.Value) { part }
                : kv.Value);

        var newParams = Parameters.ToDictionary(
            kv => kv.Key,
            kv => kv.Key == pos && parameter is not null
                ? new List<object?>(kv.Value) { parameter }
                : kv.Value);

        return new QueryState(newParts, newParams);
    }

    public bool HasAny(QueryPosition pos) => Parts[pos].Count > 0;

    public QueryState EnsureProjectionSelected(Document doc)
    {
        if (HasAny(QueryPosition.SELECT))
            return this;

        var projection = string.Join(", ",
            doc.Columns.Select(kvp =>
                $"\"{kvp.Value}\" AS \"{kvp.Key}\""));

        return With(QueryPosition.SELECT, projection);
    }
}

/// <summary>
/// PostgreSQL vault.
/// </summary>
public class PgVault<TVaultModel> : IVault<TVaultModel>
    where TVaultModel : class, IVaultModel
{
    protected readonly IServiceProvider _services;
    protected readonly ISqlDatabaseProvider _database;
    protected readonly QueryState _state;

    public IKeyspace Keyspace { get; }
    public Document VaultDocument { get; }

    internal ISqlDatabaseProvider DatabaseProvider => _database;

    private IHistoricalVault<TVaultModel>? _history;

    public IHistoricalVault<TVaultModel> History
    {
        get
        {
            if (!VaultDocument.StoreHistory)
                throw new InvalidOperationException("History is not enabled for this document.");

            return _history ??= new PgHistoricalVault<TVaultModel>(this);
        }
    }

    private static readonly Action<object, DateTime>? _timestampSetter =
        TimestampSetterFactory.Create(typeof(TVaultModel));

    public PgVault(
        ISqlDatabaseProvider database,
        IKeyspace keyspace,
        Document document,
        IServiceProvider services)
        : this(database, keyspace, document, services, new QueryState())
    {
    }

    protected PgVault(
        ISqlDatabaseProvider database,
        IKeyspace keyspace,
        Document document,
        IServiceProvider services,
        QueryState state)
    {
        _database = database;
        _services = services;
        Keyspace = keyspace;
        VaultDocument = document;
        _state = state;
    }

    private PgVault<TVaultModel> New(QueryState state)
        => new PgVault<TVaultModel>(_database, Keyspace, VaultDocument, _services, state);

    // ---------------- Query ops ----------------

    public IVault<TVaultModel> Where(Expression<Func<TVaultModel, bool>> predicate)
    {
        var where = PgQueryTranslator.Where(predicate, VaultDocument);
        return New(_state.With(QueryPosition.WHERE, where)
                         .EnsureProjectionSelected(VaultDocument));
    }

    public IVault<TVaultModel> OrderBy<TKey>(Expression<Func<TVaultModel, TKey>> key)
    {
        var clause = PgQueryTranslator.OrderBy(key, VaultDocument);
        return New(_state.With(QueryPosition.ORDER_BY, clause)
                         .With(QueryPosition.SELECT, clause));
    }

    public IVault<TVaultModel> OrderByDescending<TKey>(Expression<Func<TVaultModel, TKey>> key)
    {
        var clause = PgQueryTranslator.OrderBy(key, VaultDocument);
        return New(_state.With(QueryPosition.ORDER_BY, clause + " DESC")
                         .With(QueryPosition.SELECT, clause));
    }

    public IVault<TVaultModel> Take(int count)
        => New(_state.With(QueryPosition.LIMIT, $"LIMIT {count}")
                     .EnsureProjectionSelected(VaultDocument));

    public IVault<TVaultModel> Skip(int count)
        => New(_state.With(QueryPosition.OFFSET, $"OFFSET {count}")
                     .EnsureProjectionSelected(VaultDocument));

    // ---------------- Execution ----------------

    public async Task<List<TVaultModel>> ToListAsync()
    {
        var st = _state.EnsureProjectionSelected(VaultDocument);
        return (await _database.QueryAsync<TVaultModel>(BuildSelect(st))).ToList();
    }

    public async Task<List<TVaultModel>> ToListAsync(
        Expression<Func<TVaultModel, bool>> predicate)
        => await Where(predicate).ToListAsync();

    public async Task<TVaultModel?> FirstOrDefaultAsync()
        => (await Take(1).ToListAsync()).FirstOrDefault();

    public async Task<TVaultModel?> FirstAsync()
        => (await Take(1).ToListAsync()).First();

    public async Task<long> CountAsync()
    {
        var where = string.Join(" AND ", _state.Parts[QueryPosition.WHERE]);
        var sql = string.IsNullOrEmpty(where)
            ? $"SELECT COUNT(*) FROM {QualifiedTable}"
            : $"SELECT COUNT(*) FROM {QualifiedTable} WHERE {where}";

        return await _database.ExecuteCountAsync(sql);
    }

    public async Task<bool> AnyAsync(Expression<Func<TVaultModel, bool>> predicate)
        => await Where(predicate).CountAsync() > 0;

    public async Task<IEnumerable<TResult>> SelectAsync<TResult>(
        Expression<Func<TVaultModel, TResult>> selector)
        where TResult : class, IVaultModel
    {
        var st = _state;
        foreach (var col in PgQueryTranslator.Select(selector, VaultDocument))
            st = st.With(QueryPosition.SELECT, col);

        return await _database.QueryAsync<TResult>(BuildSelect(st));
    }

    public Task<ICursor<TVaultModel>> ToCursorAsync()
        => throw new NotImplementedException();

    // ---------------- Mutations ----------------

    public async Task SaveAsync(TVaultModel entity, bool? saveHistory = false)
    {
        entity.OnSave();
        _timestampSetter?.Invoke(entity, DateTime.UtcNow);
        await _database.UpdateAsync(entity);
    }

    public async Task SaveBatchAsync(IEnumerable<TVaultModel> entities, bool? saveHistory = false)
    {
        foreach (var e in entities)
            await SaveAsync(e, saveHistory);
    }

    public async Task<long> UpdateAsync(
        Expression<Func<SetPropertyCalls<TVaultModel>, SetPropertyCalls<TVaultModel>>> set)
        => await _database.ExecuteAsync(
            PgQueryTranslator.BuildUpdate(set, _state, VaultDocument, QualifiedTable));

    public async Task UpdateAsync(
        IReadOnlyDictionary<string, object?> primaryKey,
        IReadOnlyDictionary<string, object?> changes)
        => await _database.ExecuteAsync(
            PgQueryTranslator.BuildUpdate(primaryKey, changes, VaultDocument, QualifiedTable));

    public async Task<bool> DeleteAsync()
    {
        var where = string.Join(" AND ", _state.Parts[QueryPosition.WHERE]);
        var sql = string.IsNullOrEmpty(where)
            ? $"DELETE FROM {QualifiedTable}"
            : $"DELETE FROM {QualifiedTable} WHERE {where}";

        return await _database.ExecuteAsync(sql) > 0;
    }

    // ---------------- SQL helpers ----------------

    private string BuildSelect(QueryState st)
    {
        var sql = $"SELECT {string.Join(", ", st.Parts[QueryPosition.SELECT])} FROM {QualifiedTable}";

        if (st.Parts[QueryPosition.WHERE].Count > 0)
            sql += " WHERE " + string.Join(" AND ", st.Parts[QueryPosition.WHERE]);

        if (st.Parts[QueryPosition.ORDER_BY].Count > 0)
            sql += " ORDER BY " + string.Join(", ", st.Parts[QueryPosition.ORDER_BY]);

        if (st.Parts[QueryPosition.LIMIT].Count > 0)
            sql += " " + string.Join(" ", st.Parts[QueryPosition.LIMIT]);

        if (st.Parts[QueryPosition.OFFSET].Count > 0)
            sql += " " + string.Join(" ", st.Parts[QueryPosition.OFFSET]);

        return sql;
    }

    private string QualifiedTable
        => $"\"{Keyspace.Name}\".\"{VaultDocument.Name}\"";
}

internal static class TimestampSetterFactory
{
    private static readonly ConcurrentDictionary<Type, Action<object, DateTime>?> Cache = new();

    public static Action<object, DateTime>? Create(Type modelType)
        => Cache.GetOrAdd(modelType, t =>
        {
            var prop = t.GetProperty("Timestamp",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

            if (prop == null || prop.PropertyType != typeof(DateTime) || !prop.CanWrite)
                return null;

            var target = Expression.Parameter(typeof(object));
            var value = Expression.Parameter(typeof(DateTime));
            var assign = Expression.Assign(
                Expression.Property(Expression.Convert(target, t), prop),
                value);

            return Expression.Lambda<Action<object, DateTime>>(assign, target, value).Compile();
        });
}
