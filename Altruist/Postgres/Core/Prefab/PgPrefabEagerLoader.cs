// PgPrefabEagerLoader.cs
/*
Copyright 2025 Aron Gere

Licensed under the Apache License, Version 2.0 (the "License");
you may not use this file except in compliance with the License.
You may obtain a copy of the License at

    http://www.apache.org/licenses/LICENSE-2.0
*/

using System.Collections;
using System.Collections.Concurrent;
using System.Linq.Expressions;


namespace Altruist.Persistence;

internal sealed class PgPrefabEagerLoader
{
    private readonly ISqlDatabaseProvider _db;
    private readonly PrefabMeta _prefab;

    // (targetType, propName) => getter/setter compiled once
    private static readonly ConcurrentDictionary<(Type Target, string Prop), Func<object, object?>> _getterCache = new();
    private static readonly ConcurrentDictionary<(Type Target, string Prop), Action<object, object?>> _setterCache = new();

    // elementType => () => new List<elementType>() compiled once
    private static readonly ConcurrentDictionary<Type, Func<IList>> _listFactoryCache = new();

    public PgPrefabEagerLoader(ISqlDatabaseProvider db, PrefabMeta prefab)
    {
        _db = db;
        _prefab = prefab;
    }

    public async Task HydrateAsync<TPrefab>(List<TPrefab> prefabs, HashSet<string> includes, CancellationToken ct)
        where TPrefab : PrefabModel, new()
    {
        // Root already set. Hydrate included refs.
        foreach (var inc in includes)
        {
            ct.ThrowIfCancellationRequested();

            if (!_prefab.ComponentsByName.TryGetValue(inc, out var comp))
                continue;

            if (comp.Kind == PrefabComponentKind.Collection)
                await HydrateCollectionAsync(prefabs, comp, ct).ConfigureAwait(false);
            else if (comp.Kind == PrefabComponentKind.Single)
                await HydrateSingleAsync(prefabs, comp, ct).ConfigureAwait(false);
        }
    }

    private async Task HydrateCollectionAsync<TPrefab>(List<TPrefab> prefabs, PrefabComponentMeta comp, CancellationToken ct)
    where TPrefab : PrefabModel, new()
    {
        // Root ids (StorageId)
        var rootGetter = GetGetter(typeof(TPrefab), _prefab.RootPropertyName);

        var rootIds = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var p in prefabs)
        {
            ct.ThrowIfCancellationRequested();

            if (rootGetter(p!) is not IVaultModel root)
                continue;

            var id = root.StorageId;
            if (string.IsNullOrWhiteSpace(id))
                continue;

            if (seen.Add(id))
                rootIds.Add(id);
        }

        if (rootIds.Count == 0)
            return;

        var depType = comp.ComponentType;
        var depDoc = VaultDocument.From(depType);

        var depTable = depDoc.QualifiedTable();
        var fkCol = depDoc.Col(comp.ForeignKeyPropertyName);

        // Explicit SELECT with aliases so mapping is stable
        var select = BuildSelectAllAliased(depDoc, "d");

        string sql;
        List<object?> parameters;

        if (rootIds.Count == 1)
        {
            sql = $"SELECT {select} FROM {depTable} d WHERE d.{VaultDocument.Quote(fkCol)} = ?";
            parameters = new List<object?>(1) { rootIds[0] };
        }
        else
        {
            var inSql = string.Join(", ", Enumerable.Repeat("?", rootIds.Count));
            sql = $"SELECT {select} FROM {depTable} d WHERE d.{VaultDocument.Quote(fkCol)} IN ({inSql})";
            parameters = rootIds.Cast<object?>().ToList();
        }

        var depRows = await _db.QueryAsync(depType, sql, parameters, ct).ConfigureAwait(false);
        if (depRows.Count == 0)
        {
            SetEmptyCollections(prefabs, comp, depType);
            return;
        }

        // group by FK (dependent FK == root StorageId)
        var depFkGetter = GetGetter(depType, comp.ForeignKeyPropertyName);

        var grouped = new Dictionary<string, List<object>>(StringComparer.Ordinal);
        foreach (var row in depRows)
        {
            ct.ThrowIfCancellationRequested();

            var fk = depFkGetter(row) as string;
            if (string.IsNullOrWhiteSpace(fk))
                continue;

            if (!grouped.TryGetValue(fk!, out var list))
            {
                list = new List<object>();
                grouped[fk!] = list;
            }

            list.Add(row);
        }

        var prefabSetter = GetSetter(typeof(TPrefab), comp.Property.Name);
        var listFactory = GetListFactory(depType);

        foreach (var p in prefabs)
        {
            ct.ThrowIfCancellationRequested();

            var root = rootGetter(p!) as IVaultModel;
            var id = root?.StorageId ?? "";

            var items = grouped.TryGetValue(id, out var list) ? list : null;

            var typedList = listFactory();
            if (items is not null)
            {
                foreach (var it in items)
                    typedList.Add(it);
            }

            prefabSetter(p!, typedList);
        }
    }

    private static string BuildSelectAllAliased(VaultDocument doc, string alias)
    {
        static string QuoteIdent(string s) => $"\"{s.Replace("\"", "\"\"")}\"";

        var cols = new List<string>(doc.Columns.Count);
        foreach (var kvp in doc.Columns)
        {
            var clrProp = kvp.Key;
            var sqlCol = kvp.Value;

            cols.Add($"{alias}.{VaultDocument.Quote(sqlCol)} AS {QuoteIdent(clrProp)}");
        }

        return string.Join(", ", cols);
    }

    private void SetEmptyCollections<TPrefab>(List<TPrefab> prefabs, PrefabComponentMeta comp, Type elementType)
        where TPrefab : PrefabModel, new()
    {
        var prefabSetter = GetSetter(typeof(TPrefab), comp.Property.Name);
        var listFactory = GetListFactory(elementType);

        foreach (var p in prefabs)
        {
            var typedList = listFactory();
            prefabSetter(p!, typedList);
        }
    }

    private async Task HydrateSingleAsync<TPrefab>(List<TPrefab> prefabs, PrefabComponentMeta comp, CancellationToken ct)
    where TPrefab : PrefabModel, new()
    {
        var rootType = _prefab.RootComponentType;
        var rootFkGetter = GetGetter(rootType, comp.ForeignKeyPropertyName);

        var rootGetter = GetGetter(typeof(TPrefab), _prefab.RootPropertyName);

        var fkValues = new List<string>();
        var fkSeen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var p in prefabs)
        {
            ct.ThrowIfCancellationRequested();

            if (rootGetter(p!) is not IVaultModel root)
                continue;

            var fk = rootFkGetter(root) as string;
            if (string.IsNullOrWhiteSpace(fk))
                continue;

            if (fkSeen.Add(fk!))
                fkValues.Add(fk!);
        }

        if (fkValues.Count == 0)
            return;

        var depType = comp.ComponentType;
        var depDoc = VaultDocument.From(depType);

        var depTable = depDoc.QualifiedTable();
        var pkCol = depDoc.Col(comp.PrincipalKeyPropertyName);

        // Explicit SELECT with aliases
        var select = BuildSelectAllAliased(depDoc, "c");

        string sql;
        List<object?> parameters;

        if (fkValues.Count == 1)
        {
            sql = $"SELECT {select} FROM {depTable} c WHERE c.{VaultDocument.Quote(pkCol)} = ?";
            parameters = new List<object?>(1) { fkValues[0] };
        }
        else
        {
            var inSql = string.Join(", ", Enumerable.Repeat("?", fkValues.Count));
            sql = $"SELECT {select} FROM {depTable} c WHERE c.{VaultDocument.Quote(pkCol)} IN ({inSql})";
            parameters = fkValues.Cast<object?>().ToList();
        }

        var rows = await _db.QueryAsync(depType, sql, parameters, ct).ConfigureAwait(false);
        if (rows.Count == 0)
            return;

        var depPkGetter = GetGetter(depType, comp.PrincipalKeyPropertyName);

        var byPk = new Dictionary<string, object>(StringComparer.Ordinal);
        foreach (var row in rows)
        {
            ct.ThrowIfCancellationRequested();

            var pk = depPkGetter(row) as string;
            if (string.IsNullOrWhiteSpace(pk))
                continue;

            byPk[pk!] = row;
        }

        var prefabSetter = GetSetter(typeof(TPrefab), comp.Property.Name);

        foreach (var p in prefabs)
        {
            ct.ThrowIfCancellationRequested();

            if (rootGetter(p!) is not IVaultModel root)
                continue;

            var fk = rootFkGetter(root) as string;
            if (string.IsNullOrWhiteSpace(fk))
                continue;

            if (byPk.TryGetValue(fk!, out var depObj))
                prefabSetter(p!, depObj);
        }
    }

    // --------------------- fast cached member access ---------------------

    private static Func<object, object?> GetGetter(Type targetType, string propName)
    {
        return _getterCache.GetOrAdd((targetType, propName), static key =>
        {
            var (t, name) = key;

            var target = Expression.Parameter(typeof(object), "target");
            var castTarget = Expression.Convert(target, t);

            var member = Expression.PropertyOrField(castTarget, name);
            var box = Expression.Convert(member, typeof(object));

            return Expression.Lambda<Func<object, object?>>(box, target).Compile();
        });
    }

    private static Action<object, object?> GetSetter(Type targetType, string propName)
    {
        return _setterCache.GetOrAdd((targetType, propName), static key =>
        {
            var (t, name) = key;

            var target = Expression.Parameter(typeof(object), "target");
            var value = Expression.Parameter(typeof(object), "value");

            var castTarget = Expression.Convert(target, t);
            var member = Expression.PropertyOrField(castTarget, name);

            var castValue = Expression.Convert(value, member.Type);
            var assign = Expression.Assign(member, castValue);

            return Expression.Lambda<Action<object, object?>>(assign, target, value).Compile();
        });
    }

    private static Func<IList> GetListFactory(Type elementType)
    {
        return _listFactoryCache.GetOrAdd(elementType, static t =>
        {
            var listType = typeof(List<>).MakeGenericType(t);

            var ctor = listType.GetConstructor(Type.EmptyTypes)
                ?? throw new InvalidOperationException($"Missing parameterless ctor for {listType.Name}.");

            var newExpr = Expression.New(ctor);
            var cast = Expression.Convert(newExpr, typeof(IList));

            return Expression.Lambda<Func<IList>>(cast).Compile();
        });
    }
}
