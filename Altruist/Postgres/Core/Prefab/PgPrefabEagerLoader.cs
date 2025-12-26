using System.Reflection;

using Altruist.UORM;

namespace Altruist.Persistence;

internal sealed class PgPrefabEagerLoader
{
    private readonly ISqlDatabaseProvider _db;
    private readonly PrefabMeta _prefab;

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
                await HydrateCollectionAsync(prefabs, comp, ct);

            if (comp.Kind == PrefabComponentKind.Single)
                await HydrateSingleAsync(prefabs, comp, ct);
        }
    }

    private async Task HydrateCollectionAsync<TPrefab>(List<TPrefab> prefabs, PrefabComponentMeta comp, CancellationToken ct)
        where TPrefab : PrefabModel, new()
    {
        // Root ids
        var rootIds = prefabs
            .Select(p => GetRoot(p)?.StorageId)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        if (rootIds.Count == 0)
            return;

        var depType = comp.ComponentType;
        var depDoc = Document.From(depType);

        var depTable = QualifiedTable(depType, depDoc);
        var fkCol = Col(depDoc, comp.ForeignKeyPropertyName);

        var inSql = string.Join(", ", Enumerable.Repeat("?", rootIds.Count));
        var sql = $"SELECT * FROM {depTable} d WHERE d.{Quote(fkCol)} IN ({inSql})";

        var depRows = await QueryTypedList(depType, sql, rootIds.Cast<object?>().ToList());

        // group by FK (dependent FK == root StorageId)
        var fkProp = depType.GetProperty(comp.ForeignKeyPropertyName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException($"{depType.Name} missing FK property {comp.ForeignKeyPropertyName}");

        var grouped = depRows
            .GroupBy(r => (string?)fkProp.GetValue(r), StringComparer.Ordinal)
            .ToDictionary(g => g.Key ?? "", g => g.ToList(), StringComparer.Ordinal);

        foreach (var p in prefabs)
        {
            var id = GetRoot(p)?.StorageId ?? "";
            if (!grouped.TryGetValue(id, out var list))
                list = new List<object>();

            SetEnumerableProperty(p, comp.Property, depType, list);
        }
    }

    private async Task HydrateSingleAsync<TPrefab>(List<TPrefab> prefabs, PrefabComponentMeta comp, CancellationToken ct)
        where TPrefab : PrefabModel, new()
    {
        // Single ref:
        // root has FK property (comp.ForeignKeyPropertyName) that points to dependent PK.
        var rootType = _prefab.RootComponentType;
        var rootDoc = Document.From(rootType);

        var rootFkProp = rootType.GetProperty(comp.ForeignKeyPropertyName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException($"{rootType.Name} missing FK property {comp.ForeignKeyPropertyName}");

        var fkValues = prefabs
            .Select(p => GetRoot(p))
            .Where(r => r != null)
            .Select(r => (string?)rootFkProp.GetValue(r!))
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        if (fkValues.Count == 0)
            return;

        var depType = comp.ComponentType;
        var depDoc = Document.From(depType);

        var depTable = QualifiedTable(depType, depDoc);
        var pkCol = Col(depDoc, comp.PrincipalKeyPropertyName);

        var inSql = string.Join(", ", Enumerable.Repeat("?", fkValues.Count));
        var sql = $"SELECT * FROM {depTable} c WHERE c.{Quote(pkCol)} IN ({inSql})";

        var rows = await QueryTypedList(depType, sql, fkValues.Cast<object?>().ToList());

        var pkProp = depType.GetProperty(comp.PrincipalKeyPropertyName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException($"{depType.Name} missing PK property {comp.PrincipalKeyPropertyName}");

        var byPk = rows.ToDictionary(r => (string?)pkProp.GetValue(r) ?? "", r => r, StringComparer.Ordinal);

        foreach (var p in prefabs)
        {
            var root = GetRoot(p);
            if (root is null)
                continue;

            var fk = (string?)rootFkProp.GetValue(root);
            if (string.IsNullOrWhiteSpace(fk))
                continue;

            if (byPk.TryGetValue(fk!, out var depObj))
                comp.Property.SetValue(p, depObj);
        }
    }

    private IVaultModel? GetRoot<TPrefab>(TPrefab prefab) where TPrefab : PrefabModel, new()
    {
        var p = typeof(TPrefab).GetProperty(_prefab.RootPropertyName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException($"Root property '{_prefab.RootPropertyName}' missing on {typeof(TPrefab).Name}.");

        return (IVaultModel?)p.GetValue(prefab);
    }

    private static void SetEnumerableProperty<TPrefab>(TPrefab prefab, PropertyInfo prop, Type elementType, List<object> items)
    {
        var listType = typeof(List<>).MakeGenericType(elementType);
        var list = (System.Collections.IList)Activator.CreateInstance(listType)!;

        foreach (var it in items)
            list.Add(it);

        if (!prop.PropertyType.IsAssignableFrom(listType))
        {
            // allow assigning List<T> into IReadOnlyList<T>/IEnumerable<T>
            if (!prop.PropertyType.IsAssignableFrom(typeof(IEnumerable<>).MakeGenericType(elementType)))
                throw new InvalidOperationException($"Property {prop.Name} is not compatible with {listType.Name}.");
        }

        prop.SetValue(prefab, list);
    }

    private async Task<List<object>> QueryTypedList(Type modelType, string sql, List<object?> parameters)
    {
        var methods = _db.GetType().GetMethods(BindingFlags.Instance | BindingFlags.Public);

        var candidates = methods
            .Where(m => m.Name == nameof(ISqlDatabaseProvider.QueryAsync) && m.IsGenericMethodDefinition)
            .ToList();

        if (candidates.Count == 0)
            throw new InvalidOperationException("No QueryAsync<T> method found on ISqlDatabaseProvider implementation.");

        object? taskObj = null;

        foreach (var m in candidates)
        {
            var gm = m.MakeGenericMethod(modelType);
            var ps = gm.GetParameters();

            try
            {
                if (ps.Length == 1)
                {
                    taskObj = gm.Invoke(_db, new object?[] { sql });
                    if (taskObj != null)
                        break;
                }
                else if (ps.Length == 2)
                {
                    var pType = ps[1].ParameterType;

                    if (typeof(List<object>).IsAssignableFrom(pType))
                    {
                        var list = parameters.Select(x => x ?? DBNull.Value).Cast<object>().ToList();
                        taskObj = gm.Invoke(_db, new object?[] { sql, list });
                        if (taskObj != null)
                            break;
                    }

                    if (pType.IsArray)
                    {
                        taskObj = gm.Invoke(_db, new object?[] { sql, parameters.ToArray() });
                        if (taskObj != null)
                            break;
                    }
                }
            }
            catch
            {
                // try next
            }
        }

        if (taskObj is null)
            throw new InvalidOperationException("Could not invoke QueryAsync<T> with supported parameter shapes.");

        var task = (Task)taskObj;
        await task.ConfigureAwait(false);

        var resultProp = taskObj.GetType().GetProperty("Result")
            ?? throw new InvalidOperationException("QueryAsync Task has no Result.");

        var enumerable = (System.Collections.IEnumerable)resultProp.GetValue(taskObj)!;

        var outList = new List<object>();
        foreach (var it in enumerable)
            outList.Add(it!);

        return outList;
    }

    private static string QualifiedTable(Type modelType, Document doc)
    {
        var va = modelType.GetCustomAttribute<VaultAttribute>(inherit: true);
        var schema = string.IsNullOrWhiteSpace(va?.Keyspace) ? "public" : va!.Keyspace!.Trim();
        return $"{Quote(schema)}.{Quote(doc.Name)}";
    }

    private static string Col(Document doc, string logical)
        => doc.Columns.TryGetValue(logical, out var physical) ? physical : Document.ToCamelCase(logical);

    private static string Quote(string s) => $"\"{s.Replace("\"", "\"\"")}\"";
}
