using System.Reflection;

using Altruist.Persistence.Postgres;

namespace Altruist.Persistence;

internal sealed class PgPrefabEagerLoader
{
    private readonly ISqlDatabaseProvider _db;
    private readonly IPgModelSqlMetadataProvider _sqlMeta;
    private readonly PrefabMeta _prefab;

    public PgPrefabEagerLoader(ISqlDatabaseProvider db, IPgModelSqlMetadataProvider sqlMeta, PrefabMeta prefab)
    {
        _db = db;
        _sqlMeta = sqlMeta;
        _prefab = prefab;
    }

    public async Task HydrateAsync<TPrefab>(List<TPrefab> prefabs, HashSet<string> includes, CancellationToken ct)
        where TPrefab : PrefabModel, new()
    {
        foreach (var inc in includes)
        {
            if (!_prefab.ComponentsByName.TryGetValue(inc, out var comp))
                continue;

            // Root already loaded by the root query
            if (string.Equals(inc, _prefab.RootPropertyName, StringComparison.Ordinal))
                continue;

            if (comp.Kind == PrefabComponentKind.Collection)
                await HydrateCollectionAsync(prefabs, comp).ConfigureAwait(false);
            else
                await HydrateSingleAsync(prefabs, comp).ConfigureAwait(false);
        }
    }

    private async Task HydrateCollectionAsync<TPrefab>(List<TPrefab> prefabs, PrefabComponentMeta comp)
        where TPrefab : PrefabModel, new()
    {
        // principal is root (no nesting)
        var rootIds = prefabs
            .Select(p => GetRootComponent(p)?.StorageId)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        if (rootIds.Count == 0)
            return;

        var depSql = _sqlMeta.Get(comp.ComponentType);
        var depDoc = depSql.Document;

        var fkLogical = comp.ForeignKeyPropertyName;
        var fkCol = depDoc.Columns.TryGetValue(fkLogical, out var fkPhys)
            ? fkPhys
            : Document.ToCamelCase(fkLogical);

        var inSql = string.Join(", ", Enumerable.Repeat("?", rootIds.Count));
        var sql = $"SELECT * FROM {depSql.QualifiedTable} d WHERE d.\"{fkCol}\" IN ({inSql})";

        var depRows = await QueryTypedList(depSql.ModelType, sql, rootIds.Cast<object?>().ToArray()).ConfigureAwait(false);

        var fkProp = depSql.ModelType.GetProperty(fkLogical, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                    ?? throw new InvalidOperationException($"{depSql.ModelType.Name} missing FK property {fkLogical}.");

        var grouped = depRows
            .GroupBy(r => (string?)fkProp.GetValue(r) ?? "", StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);

        foreach (var p in prefabs)
        {
            var id = GetRootComponent(p)?.StorageId ?? "";
            grouped.TryGetValue(id, out var list);
            list ??= new List<object>();

            SetCollectionProperty(p, comp.Property, depSql.ModelType, list);
        }
    }

    private async Task HydrateSingleAsync<TPrefab>(List<TPrefab> prefabs, PrefabComponentMeta comp)
        where TPrefab : PrefabModel, new()
    {
        // Single ref rule:
        // FK is on ROOT (principal/root) pointing to referenced component PK.
        // Example:
        //   CharacterVault.GuildId (FK) -> GuildVault.StorageId (PK)
        var rootType = _prefab.RootComponentType;

        var fkPropOnRoot = rootType.GetProperty(comp.ForeignKeyPropertyName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                          ?? throw new InvalidOperationException($"{rootType.Name} missing FK property {comp.ForeignKeyPropertyName}.");

        var fkValues = prefabs
            .Select(p => GetRootComponent(p))
            .Where(r => r != null)
            .Select(r => (string?)fkPropOnRoot.GetValue(r!))
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        if (fkValues.Count == 0)
        {
            // No FK values => set nulls so user doesn't see stale data
            foreach (var p in prefabs)
                comp.Property.SetValue(p, null);

            return;
        }

        var refSql = _sqlMeta.Get(comp.ComponentType);
        var refDoc = refSql.Document;

        var pkLogical = comp.PrincipalKeyPropertyName;
        var pkCol = refDoc.Columns.TryGetValue(pkLogical, out var pkPhys)
            ? pkPhys
            : Document.ToCamelCase(pkLogical);

        var inSql = string.Join(", ", Enumerable.Repeat("?", fkValues.Count));
        var sql = $"SELECT * FROM {refSql.QualifiedTable} x WHERE x.\"{pkCol}\" IN ({inSql})";

        var rows = await QueryTypedList(refSql.ModelType, sql, fkValues.Cast<object?>().ToArray()).ConfigureAwait(false);

        var pkProp = refSql.ModelType.GetProperty(pkLogical, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                    ?? throw new InvalidOperationException($"{refSql.ModelType.Name} missing PK property {pkLogical}.");

        var byPk = rows.ToDictionary(r => (string?)pkProp.GetValue(r) ?? "", r => r, StringComparer.Ordinal);

        foreach (var p in prefabs)
        {
            var root = GetRootComponent(p);
            if (root is null)
            {
                comp.Property.SetValue(p, null);
                continue;
            }

            var fk = (string?)fkPropOnRoot.GetValue(root);
            if (string.IsNullOrWhiteSpace(fk))
            {
                comp.Property.SetValue(p, null);
                continue;
            }

            comp.Property.SetValue(p, byPk.TryGetValue(fk, out var obj) ? obj : null);
        }
    }

    private IVaultModel? GetRootComponent<TPrefab>(TPrefab prefab) where TPrefab : PrefabModel, new()
    {
        var p = typeof(TPrefab).GetProperty(_prefab.RootPropertyName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException($"Root property '{_prefab.RootPropertyName}' missing on {typeof(TPrefab).Name}.");
        return (IVaultModel?)p.GetValue(prefab);
    }

    private static void SetCollectionProperty<TPrefab>(TPrefab prefab, PropertyInfo prop, Type elementType, List<object> items)
    {
        // Support List<T>, IReadOnlyList<T>, IEnumerable<T> (but we materialize a List<T>)
        if (!prop.PropertyType.IsGenericType)
            throw new InvalidOperationException($"Property {prop.Name} must be a generic collection type.");

        var genDef = prop.PropertyType.GetGenericTypeDefinition();
        var elem = prop.PropertyType.GetGenericArguments()[0];

        if (elem != elementType)
            throw new InvalidOperationException($"Property {prop.Name} element type must be {elementType.Name}.");

        if (genDef != typeof(List<>) && genDef != typeof(IReadOnlyList<>) && genDef != typeof(IEnumerable<>))
            throw new InvalidOperationException($"Property {prop.Name} must be List<T>, IReadOnlyList<T>, or IEnumerable<T>.");

        var listType = typeof(List<>).MakeGenericType(elementType);
        var list = (System.Collections.IList)Activator.CreateInstance(listType)!;

        foreach (var it in items)
            list.Add(it);

        prop.SetValue(prefab, list);
    }

    private async Task<List<object>> QueryTypedList(Type modelType, string sql, object?[] parameters)
    {
        // Find ISqlDatabaseProvider.QueryAsync<T>(string) or QueryAsync<T>(string, object?[])
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
                    break;
                }

                if (ps.Length == 2)
                {
                    taskObj = gm.Invoke(_db, new object?[] { sql, parameters });
                    break;
                }
            }
            catch
            {
                // try next overload
            }
        }

        if (taskObj is null)
            throw new InvalidOperationException("Could not invoke QueryAsync<T> with supported parameter shapes.");

        var task = (Task)taskObj;
        await task.ConfigureAwait(false);

        var resultProp = taskObj.GetType().GetProperty("Result")
                         ?? throw new InvalidOperationException("QueryAsync Task has no Result.");

        var enumerable = (System.Collections.IEnumerable)resultProp.GetValue(taskObj)!;

        var list = new List<object>();
        foreach (var it in enumerable)
            list.Add(it!);

        return list;
    }
}
