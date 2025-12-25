using System.Collections.Concurrent;
using System.Linq.Expressions;
using System.Reflection;
using System.Text;
using System.Text.Json;

using Altruist.UORM;

namespace Altruist.Persistence;

public static class PrefabMetadataRegistry
{
    private static readonly ConcurrentDictionary<Type, PrefabComponentMetadata[]> _byPrefab = new();

    public static IReadOnlyList<PrefabComponentMetadata> GetComponents(Type prefabType)
        => _byPrefab.TryGetValue(prefabType, out var arr) ? arr : Array.Empty<PrefabComponentMetadata>();

    public static void RegisterPrefab(Type prefabType)
    {
        if (prefabType is null)
            throw new ArgumentNullException(nameof(prefabType));
        if (_byPrefab.ContainsKey(prefabType))
            return;

        var list = new List<PrefabComponentMetadata>();

        // 1) Scan [PrefabComponent] properties (handle properties)
        foreach (var prop in prefabType.GetProperties(
                     BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
        {
            var attr = prop.GetCustomAttribute<PrefabComponentAttribute>(inherit: false);
            if (attr is null)
                continue;

            if (!prop.CanWrite)
                throw new InvalidOperationException(
                    $"Property {prefabType.FullName}.{prop.Name} marked [PrefabComponent] must be settable.");

            if (!prop.PropertyType.IsGenericType ||
                prop.PropertyType.GetGenericTypeDefinition() != typeof(IPrefabHandle<>))
            {
                throw new InvalidOperationException(
                    $"Property {prefabType.FullName}.{prop.Name} marked [PrefabComponent] " +
                    $"must be of type IPrefabHandle<TComponent>.");
            }

            if (!string.IsNullOrWhiteSpace(attr.AutoLoadOn) &&
                string.IsNullOrWhiteSpace(attr.RelationKey))
            {
                throw new InvalidOperationException(
                    $"Property {prefabType.FullName}.{prop.Name} marked [PrefabComponent] " +
                    $"has AutoLoadOn='{attr.AutoLoadOn}' but no RelationKey was specified.");
            }

            var componentType = prop.PropertyType.GetGenericArguments()[0];

            // Principal key: default to StorageId (your framework identity)
            // You MAY override via RelationKey if you want (still must be UNIQUE/PK).
            var principalKey = string.IsNullOrWhiteSpace(attr.RelationKey)
                ? nameof(IVaultModel.StorageId)
                : attr.RelationKey!.Trim();

            // Determine where the ref id should be persisted:
            // - Prefer an explicit persisted ref property if you already have one (e.g. CharacterId with [VaultColumn])
            // - Else create a shadow field/column: prefab_<component>_ref
            var explicitRefProp = FindExplicitRefProperty(prefabType, prop.Name, componentType, principalKey);

            var (refLogical, refPhysical, getRefId, setRefId, hasExplicitRef) =
                BuildRefBinding(prefabType, prop.Name, explicitRefProp);

            var setter = CompileSetter(prefabType, prop);
            var getter = CompileGetter(prefabType, prop);
            var handleFactory = CompileHandleFactory(componentType);
            var saver = CompileSaveBatchDelegate(componentType);

            var applyBulk = CompileApplyBulkToHandle(componentType);
            var deserialize = CompileDeserializeJson(componentType);

            list.Add(new PrefabComponentMetadata
            {
                Name = prop.Name,
                PrefabType = prefabType,
                ComponentType = componentType,

                // handle wiring
                Setter = setter,
                Getter = getter,
                HandleFactory = handleFactory,

                // ref column wiring
                PrincipalKeyPropertyName = principalKey,
                RefLogicalFieldName = refLogical,
                RefColumnName = refPhysical,
                HasExplicitRefProperty = hasExplicitRef,
                GetRefId = getRefId,
                SetRefId = setRefId,

                SaveBatchAsync = saver,
                ApplyBulkToHandle = applyBulk,
                DeserializeJson = deserialize,
                AutoLoadOn = attr.AutoLoadOn,
                RelationKey = attr.RelationKey,
                OnLoadedCallbacks = Array.Empty<Func<object, object?, Task>>()
            });
        }

        var metas = list.ToArray();

        // 2) Resolve [OnPrefabComponentLoad] callbacks
        var callbacksByComponent = new Dictionary<string, List<Func<object, object?, Task>>>(StringComparer.Ordinal);

        foreach (var method in prefabType.GetMethods(
                     BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
        {
            var attrs = method.GetCustomAttributes<OnPrefabComponentLoadAttribute>(inherit: false);
            foreach (var loadAttr in attrs)
            {
                var targetMeta = metas.FirstOrDefault(m =>
                    string.Equals(m.Name, loadAttr.ComponentName, StringComparison.Ordinal));

                if (targetMeta is null)
                {
                    throw new InvalidOperationException(
                        $"Method {prefabType.FullName}.{method.Name} is marked [OnPrefabComponentLoad(\"{loadAttr.ComponentName}\")] " +
                        $"but no [PrefabComponent] named '{loadAttr.ComponentName}' exists.");
                }

                var cb = CompileOnComponentLoadCallback(prefabType, method, targetMeta.ComponentType);
                if (!callbacksByComponent.TryGetValue(targetMeta.Name, out var listForComponent))
                {
                    listForComponent = new List<Func<object, object?, Task>>();
                    callbacksByComponent[targetMeta.Name] = listForComponent;
                }
                listForComponent.Add(cb);
            }
        }

        // 3) Store finalized metadata (with callbacks)
        var finalList = new List<PrefabComponentMetadata>(metas.Length);
        foreach (var meta in metas)
        {
            callbacksByComponent.TryGetValue(meta.Name, out var cbList);

            finalList.Add(meta with
            {
                OnLoadedCallbacks = cbList?.ToArray() ?? Array.Empty<Func<object, object?, Task>>()
            });
        }

        _byPrefab[prefabType] = finalList.ToArray();
    }

    // ----------------- ref binding -----------------

    private static PropertyInfo? FindExplicitRefProperty(
        Type prefabType,
        string componentName,
        Type componentType,
        string principalKeyPropertyName)
    {
        // Heuristics:
        // 1) property named "<ComponentName>Id" with [VaultColumn]
        // 2) any property with [VaultForeignKey] pointing to componentType
        // 3) any property with [VaultColumn] named "prefab_<component>_ref" (already exists)

        var props = prefabType.GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

        // 2) FK attribute match (strongest)
        foreach (var p in props)
        {
            if (p.GetCustomAttribute<VaultIgnoreAttribute>(inherit: false) is not null)
                continue;

            var col = p.GetCustomAttribute<VaultColumnAttribute>(inherit: false);
            if (col is null)
                continue;

            var fk = p.GetCustomAttribute<VaultForeignKeyAttribute>(inherit: false);
            if (fk is null)
                continue;

            if (fk.PrincipalType == componentType &&
                string.Equals(fk.PrincipalPropertyName, principalKeyPropertyName, StringComparison.OrdinalIgnoreCase))
                return p;

            // allow FK without specifying principal key exactly (common)
            if (fk.PrincipalType == componentType &&
                string.Equals(principalKeyPropertyName, nameof(IVaultModel.StorageId), StringComparison.OrdinalIgnoreCase))
                return p;
        }

        // 1) "<ComponentName>Id"
        var candidateName = componentName + "Id";
        var byName = props.FirstOrDefault(p =>
            string.Equals(p.Name, candidateName, StringComparison.Ordinal) &&
            p.GetCustomAttribute<VaultColumnAttribute>(inherit: false) is not null &&
            p.GetCustomAttribute<VaultIgnoreAttribute>(inherit: false) is null);

        if (byName is not null)
            return byName;

        // 3) physical name match
        var expectedPhysical = BuildRefColumnName(componentName);
        var byPhysical = props.FirstOrDefault(p =>
        {
            var col = p.GetCustomAttribute<VaultColumnAttribute>(inherit: false);
            return col is not null &&
                   string.Equals(col.Name, expectedPhysical, StringComparison.OrdinalIgnoreCase) &&
                   p.GetCustomAttribute<VaultIgnoreAttribute>(inherit: false) is null;
        });

        return byPhysical;
    }

    private static (string RefLogical, string RefPhysical,
                    Func<PrefabModel, string?> GetRefId,
                    Action<PrefabModel, string?> SetRefId,
                    bool HasExplicit)
        BuildRefBinding(Type prefabType, string componentName, PropertyInfo? explicitRefProp)
    {
        if (explicitRefProp is not null)
        {
            if (explicitRefProp.PropertyType != typeof(string) && explicitRefProp.PropertyType != typeof(string))
            {
                throw new InvalidOperationException(
                    $"Explicit ref property {prefabType.FullName}.{explicitRefProp.Name} must be string.");
            }

            var colAttr = explicitRefProp.GetCustomAttribute<VaultColumnAttribute>(inherit: false);
            var physical = !string.IsNullOrWhiteSpace(colAttr?.Name)
                ? colAttr!.Name!
                : ToSnakeCase(explicitRefProp.Name);

            return (explicitRefProp.Name, physical,
                CompileStringGetter(prefabType, explicitRefProp),
                CompileStringSetter(prefabType, explicitRefProp),
                HasExplicit: true);
        }

        // Shadow ref field/column (no CLR property required)
        var refLogical = $"__{componentName}Ref";
        var refPhysical = BuildRefColumnName(componentName);

        string? GetFromRefs(PrefabModel pm)
            => pm.ComponentRefs.TryGetValue(componentName, out var id) ? id : null;

        void SetToRefs(PrefabModel pm, string? id)
            => pm.ComponentRefs[componentName] = id;

        return (refLogical, refPhysical, GetFromRefs, SetToRefs, HasExplicit: false);
    }

    internal static string BuildRefColumnName(string componentName)
        => $"prefab_{ToSnakeCase(componentName)}_ref";

    private static Func<PrefabModel, string?> CompileStringGetter(Type prefabType, PropertyInfo prop)
    {
        var pm = Expression.Parameter(typeof(PrefabModel), "pm");
        var cast = Expression.Convert(pm, prefabType);
        var access = Expression.Property(cast, prop);
        var box = Expression.Convert(access, typeof(string));
        return Expression.Lambda<Func<PrefabModel, string?>>(box, pm).Compile();
    }

    private static Action<PrefabModel, string?> CompileStringSetter(Type prefabType, PropertyInfo prop)
    {
        var pm = Expression.Parameter(typeof(PrefabModel), "pm");
        var value = Expression.Parameter(typeof(string), "value");

        var cast = Expression.Convert(pm, prefabType);
        var access = Expression.Property(cast, prop);

        var assign = Expression.Assign(access, Expression.Convert(value, prop.PropertyType));
        return Expression.Lambda<Action<PrefabModel, string?>>(assign, pm, value).Compile();
    }

    // ----------------- compiled helpers -----------------

    private static Action<object, IVaultModel?> CompileApplyBulkToHandle(Type componentType)
    {
        // (object handle, IVaultModel? obj) => ((PrefabHandle<T>)handle).ApplyBulk((T?)obj)
        var handleObj = Expression.Parameter(typeof(object), "handle");
        var modelObj = Expression.Parameter(typeof(IVaultModel), "model"); // allow null at call-site

        var concreteHandleType = typeof(PrefabHandle<>).MakeGenericType(componentType);

        var castHandle = Expression.Convert(handleObj, concreteHandleType);
        var castModel = Expression.Convert(modelObj, componentType); // null ok

        var applyBulk = concreteHandleType.GetMethod("ApplyBulk", BindingFlags.Instance | BindingFlags.NonPublic);
        if (applyBulk is null)
            throw new InvalidOperationException($"PrefabHandle<{componentType.Name}> must have internal ApplyBulk.");

        var call = Expression.Call(castHandle, applyBulk, castModel);

        return Expression.Lambda<Action<object, IVaultModel?>>(call, handleObj, modelObj).Compile();
    }

    private static Func<string, JsonSerializerOptions, IVaultModel?> CompileDeserializeJson(Type componentType)
    {
        // (string json, JsonSerializerOptions opts) => (IVaultModel?)JsonSerializer.Deserialize<T>(json, opts)
        var json = Expression.Parameter(typeof(string), "json");
        var opts = Expression.Parameter(typeof(JsonSerializerOptions), "opts");

        var deserialize = typeof(JsonSerializer)
            .GetMethods(BindingFlags.Public | BindingFlags.Static)
            .First(m =>
                m.Name == nameof(JsonSerializer.Deserialize) &&
                m.IsGenericMethodDefinition &&
                m.GetParameters().Length == 2 &&
                m.GetParameters()[0].ParameterType == typeof(string) &&
                m.GetParameters()[1].ParameterType == typeof(JsonSerializerOptions))
            .MakeGenericMethod(componentType);

        var call = Expression.Call(deserialize, json, opts);
        var box = Expression.Convert(call, typeof(IVaultModel));

        return Expression.Lambda<Func<string, JsonSerializerOptions, IVaultModel?>>(box, json, opts).Compile();
    }

    private static Func<object, object?, Task> CompileOnComponentLoadCallback(
        Type prefabType,
        MethodInfo method,
        Type componentType)
    {
        var parameters = method.GetParameters();

        if (parameters.Length == 0)
        {
            throw new InvalidOperationException(
                $"Method {prefabType.FullName}.{method.Name} marked [OnPrefabComponentLoad] " +
                $"must have at least one parameter for the component being loaded.");
        }

        var firstParamType = parameters[0].ParameterType;
        if (!firstParamType.IsAssignableFrom(componentType))
        {
            throw new InvalidOperationException(
                $"Method {prefabType.FullName}.{method.Name} marked [OnPrefabComponentLoad] " +
                $"expects first parameter of type {firstParamType.Name}, but component type is {componentType.Name}.");
        }

        var returnsTask = typeof(Task).IsAssignableFrom(method.ReturnType);
        if (method.ReturnType != typeof(void) && !returnsTask)
        {
            throw new InvalidOperationException(
                $"Method {prefabType.FullName}.{method.Name} marked [OnPrefabComponentLoad] " +
                $"must return void or Task.");
        }

        return async (prefabObj, componentObj) =>
        {
            if (componentObj is null)
                return;

            var args = new object?[parameters.Length];

            for (int i = 0; i < parameters.Length; i++)
            {
                if (i == 0)
                    args[0] = componentObj;
                else
                    args[i] = Dependencies.Inject(parameters[i].ParameterType);
            }

            var result = method.Invoke(prefabObj, args);
            if (result is Task t)
                await t.ConfigureAwait(false);
        };
    }

    private static Action<object, object?> CompileSetter(Type prefabType, PropertyInfo prop)
    {
        var target = Expression.Parameter(typeof(object), "target");
        var value = Expression.Parameter(typeof(object), "value");

        var castTarget = Expression.Convert(target, prefabType);
        var castValue = Expression.Convert(value, prop.PropertyType);

        var body = Expression.Assign(Expression.Property(castTarget, prop), castValue);

        return Expression.Lambda<Action<object, object?>>(body, target, value).Compile();
    }

    private static Func<object, object?> CompileGetter(Type prefabType, PropertyInfo prop)
    {
        var target = Expression.Parameter(typeof(object), "target");
        var castTarget = Expression.Convert(target, prefabType);
        var body = Expression.Property(castTarget, prop);

        var boxed = Expression.Convert(body, typeof(object));
        return Expression.Lambda<Func<object, object?>>(boxed, target).Compile();
    }

    private static Func<PrefabModel, PrefabComponentMetadata, string?, object>
        CompileHandleFactory(Type componentType)
    {
        var handleClr = typeof(PrefabHandle<>).MakeGenericType(componentType);

        var ctor = handleClr.GetConstructor(
        [
            typeof(PrefabModel),
            typeof(PrefabComponentMetadata),
            typeof(string)
        ]);

        if (ctor is null)
            throw new InvalidOperationException(
                $"PrefabHandle<{componentType.Name}> must have ctor(PrefabModel, PrefabComponentMetadata, string).");

        var ownerParam = Expression.Parameter(typeof(PrefabModel), "owner");
        var metaParam = Expression.Parameter(typeof(PrefabComponentMetadata), "meta");
        var idParam = Expression.Parameter(typeof(string), "id");

        var newExpr = Expression.New(ctor, ownerParam, metaParam, idParam);

        return Expression
            .Lambda<Func<PrefabModel, PrefabComponentMetadata, string?, object>>(
                newExpr, ownerParam, metaParam, idParam)
            .Compile();
    }

    private static Func<IReadOnlyCollection<IVaultModel>, Task>
        CompileSaveBatchDelegate(Type componentType)
    {
        // unchanged from your version
        var vaultType = typeof(IVault<>).MakeGenericType(componentType);

        var castMethod = typeof(Enumerable)
            .GetMethod(nameof(Enumerable.Cast), BindingFlags.Public | BindingFlags.Static)!
            .MakeGenericMethod(componentType);

        var toListMethod = typeof(Enumerable)
            .GetMethod(nameof(Enumerable.ToList), BindingFlags.Public | BindingFlags.Static)!
            .MakeGenericMethod(componentType);

        var enumerableOfComponent = typeof(IEnumerable<>).MakeGenericType(componentType);

        var saveBatch = vaultType.GetMethod(
                            nameof(IVault<IVaultModel>.SaveBatchAsync),
                            [enumerableOfComponent, typeof(bool?)])
                      ?? vaultType.GetMethod(
                            nameof(IVault<IVaultModel>.SaveBatchAsync),
                            [enumerableOfComponent])
                      ?? throw new InvalidOperationException(
                            $"IVault<{componentType.Name}> must have SaveBatchAsync.");

        return async items =>
        {
            if (items is null || items.Count == 0)
                return;

            var vault = Dependencies.Inject(vaultType);

            var casted = castMethod.Invoke(null, [items])!;
            var list = toListMethod.Invoke(null, [casted])!;

            object? result;
            var parameters = saveBatch.GetParameters();

            if (parameters.Length == 2)
                result = saveBatch.Invoke(vault, [list, null]);
            else
                result = saveBatch.Invoke(vault, [list]);

            if (result is Task t)
                await t.ConfigureAwait(false);
        };
    }

    private static string ToSnakeCase(string value)
    {
        if (string.IsNullOrEmpty(value))
            return value;

        var sb = new StringBuilder(value.Length + 4);
        var prevLower = false;

        for (int i = 0; i < value.Length; i++)
        {
            var c = value[i];

            if (char.IsUpper(c))
            {
                if (i > 0 && (prevLower || (i + 1 < value.Length && char.IsLower(value[i + 1]))))
                    sb.Append('_');

                sb.Append(char.ToLowerInvariant(c));
                prevLower = false;
            }
            else
            {
                sb.Append(c);
                prevLower = char.IsLetter(c) && char.IsLower(c);
            }
        }

        return sb.ToString();
    }
}

public sealed class PrefabComponentInfo
{
    public string Name { get; init; } = default!;
    public Type ComponentType { get; init; } = default!;
    public Func<object, object?> Getter { get; init; } = default!;
    public Action<object, object?> Setter { get; init; } = default!;
}

public sealed record PrefabComponentMetadata
{
    public string Name { get; init; } = default!;
    public Type PrefabType { get; init; } = default!;
    public Type ComponentType { get; init; } = default!;

    public Action<object, object?> Setter { get; init; } = default!;
    public Func<object, object?> Getter { get; init; } = default!;

    public Func<PrefabModel, PrefabComponentMetadata, string?, object> HandleFactory { get; init; } = default!;
    public Func<IReadOnlyCollection<IVaultModel>, Task> SaveBatchAsync { get; init; } = default!;

    public Action<object /*handle*/, IVaultModel? /*component*/> ApplyBulkToHandle { get; init; } = default!;
    public Func<string, JsonSerializerOptions, IVaultModel?> DeserializeJson { get; init; } = default!;

    // NEW: relational ref mapping
    public string PrincipalKeyPropertyName { get; init; } = nameof(IVaultModel.StorageId);
    public string RefLogicalFieldName { get; init; } = default!; // CLR property OR shadow field name
    public string RefColumnName { get; init; } = default!;       // physical column name
    public bool HasExplicitRefProperty { get; init; }
    public Func<PrefabModel, string?> GetRefId { get; init; } = default!;
    public Action<PrefabModel, string?> SetRefId { get; init; } = default!;

    public string? AutoLoadOn { get; init; }
    public string? RelationKey { get; init; }

    public Func<object, object?, Task>[] OnLoadedCallbacks { get; init; }
        = Array.Empty<Func<object, object?, Task>>();
}
