using System.Collections.Concurrent;
using System.Linq.Expressions;
using System.Reflection;
using System.Text.Json;

namespace Altruist.Persistence;

public static class PrefabMetadataRegistry
{
    private static readonly ConcurrentDictionary<Type, PrefabComponentMetadata[]> _byPrefab = new();

    public static IReadOnlyList<PrefabComponentMetadata> GetComponents(Type prefabType)
        => _byPrefab.TryGetValue(prefabType, out var arr) ? arr : Array.Empty<PrefabComponentMetadata>();

    public static void RegisterPrefab(Type prefabType)
    {
        if (_byPrefab.ContainsKey(prefabType))
            return;

        var list = new List<PrefabComponentMetadata>();

        foreach (var prop in prefabType.GetProperties(
                     BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
        {
            var attr = prop.GetCustomAttribute<PrefabComponentAttribute>();
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

            var setter = CompileSetter(prefabType, prop);
            var getter = CompileGetter(prefabType, prop);
            var handleFactory = CompileHandleFactory(componentType);
            var saver = CompileSaveBatchDelegate(componentType);

            // NEW: compile bulk-apply and typed deserializer once
            var applyBulk = CompileApplyBulkToHandle(componentType);
            var deserialize = CompileDeserializeJson(componentType);

            list.Add(new PrefabComponentMetadata
            {
                Name = prop.Name,
                PrefabType = prefabType,
                ComponentType = componentType,
                Setter = setter,
                Getter = getter,
                HandleFactory = handleFactory,
                SaveBatchAsync = saver,
                ApplyBulkToHandle = applyBulk,
                DeserializeJson = deserialize,
                AutoLoadOn = attr.AutoLoadOn,
                RelationKey = attr.RelationKey,
                OnLoadedCallbacks = Array.Empty<Func<object, object?, Task>>()
            });
        }

        var metas = list.ToArray();

        var callbacksByComponent = new Dictionary<string, List<Func<object, object?, Task>>>(
            StringComparer.Ordinal);

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

        var finalList = new List<PrefabComponentMetadata>(metas.Length);
        foreach (var meta in metas)
        {
            callbacksByComponent.TryGetValue(meta.Name, out var cbList);

            finalList.Add(new PrefabComponentMetadata
            {
                Name = meta.Name,
                PrefabType = meta.PrefabType,
                ComponentType = meta.ComponentType,
                Setter = meta.Setter,
                Getter = meta.Getter,
                HandleFactory = meta.HandleFactory,
                SaveBatchAsync = meta.SaveBatchAsync,
                ApplyBulkToHandle = meta.ApplyBulkToHandle,
                DeserializeJson = meta.DeserializeJson,
                AutoLoadOn = meta.AutoLoadOn,
                RelationKey = meta.RelationKey,
                OnLoadedCallbacks = cbList?.ToArray()
                    ?? Array.Empty<Func<object, object?, Task>>()
            });
        }

        _byPrefab[prefabType] = finalList.ToArray();
    }

    private static Action<object, IVaultModel?> CompileApplyBulkToHandle(Type componentType)
    {
        // (object handle, IVaultModel? obj) => ((PrefabHandle<T>)handle).ApplyBulk((T?)obj)
        var handleObj = Expression.Parameter(typeof(object), "handle");
        var modelObj = Expression.Parameter(typeof(IVaultModel), "model"); // allow null at call-site

        var concreteHandleType = typeof(PrefabHandle<>).MakeGenericType(componentType);

        var castHandle = Expression.Convert(handleObj, concreteHandleType);
        var castModel = Expression.Convert(modelObj, componentType); // null ok => null

        var applyBulk = concreteHandleType.GetMethod("ApplyBulk", BindingFlags.Instance | BindingFlags.NonPublic);
        if (applyBulk is null)
            throw new InvalidOperationException($"PrefabHandle<{componentType.Name}> must have internal ApplyBulk.");

        var call = Expression.Call(castHandle, applyBulk, castModel);

        var lambda = Expression.Lambda<Action<object, IVaultModel?>>(
            call,
            handleObj,
            modelObj);

        return lambda.Compile();
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

    // ... everything else unchanged from your file (CompileOnComponentLoadCallback / setters / getters / etc) ...

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
}

public sealed class PrefabComponentInfo
{
    public string Name { get; init; } = default!;
    public Type ComponentType { get; init; } = default!;
    public Func<object, object?> Getter { get; init; } = default!;
    public Action<object, object?> Setter { get; init; } = default!;
}

public sealed class PrefabComponentMetadata
{
    public string Name { get; init; } = default!;
    public Type PrefabType { get; init; } = default!;
    public Type ComponentType { get; init; } = default!;

    public Action<object, object?> Setter { get; init; } = default!;
    public Func<object, object?> Getter { get; init; } = default!;

    public Func<PrefabModel, PrefabComponentMetadata, string?, object> HandleFactory { get; init; } = default!;
    public Func<IReadOnlyCollection<IVaultModel>, Task> SaveBatchAsync { get; init; } = default!;

    public Action<object /*handle*/, IVaultModel? /*component*/> ApplyBulkToHandle { get; init; } = default!;
    public Func<string, System.Text.Json.JsonSerializerOptions, IVaultModel?> DeserializeJson { get; init; } = default!;

    public string? AutoLoadOn { get; init; }
    public string? RelationKey { get; init; }

    public Func<object, object?, Task>[] OnLoadedCallbacks { get; init; }
        = Array.Empty<Func<object, object?, Task>>();
}