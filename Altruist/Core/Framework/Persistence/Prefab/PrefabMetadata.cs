using System.Collections.Concurrent;
using System.Reflection;
using System.Linq.Expressions;

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

        // ---------- scan properties for [PrefabComponent] ----------
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

            // If AutoLoadOn is present, RelationKey must be provided (design requirement)
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

            list.Add(new PrefabComponentMetadata
            {
                Name = prop.Name,
                PrefabType = prefabType,
                ComponentType = componentType,
                Setter = setter,
                Getter = getter,
                HandleFactory = handleFactory,
                SaveBatchAsync = saver,
                AutoLoadOn = attr.AutoLoadOn,
                RelationKey = attr.RelationKey,
                OnLoadedCallbacks = Array.Empty<Func<object, object?, Task>>()
            });
        }

        var metas = list.ToArray();

        // ---------- scan methods for [OnPrefabComponentLoad] ----------
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

        // ---------- attach callbacks to metadata ----------
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
                AutoLoadOn = meta.AutoLoadOn,
                RelationKey = meta.RelationKey,
                OnLoadedCallbacks = cbList?.ToArray()
                    ?? Array.Empty<Func<object, object?, Task>>()
            });
        }

        _byPrefab[prefabType] = finalList.ToArray();
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

        // We build a delegate:
        // (prefabObj, componentObj, services) => { resolve extra parameters from DI; invoke method; await if Task; }
        return async (prefabObj, componentObj) =>
        {
            if (componentObj is null)
                return;

            var args = new object?[parameters.Length];

            for (int i = 0; i < parameters.Length; i++)
            {
                if (i == 0)
                {
                    args[0] = componentObj; // already the component instance
                }
                else
                {
                    var serviceType = parameters[i].ParameterType;
                    args[i] = Dependencies.Inject(serviceType);
                }
            }

            var result = method.Invoke(prefabObj, args);
            if (result is Task t)
            {
                await t.ConfigureAwait(false);
            }
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

        // (owner, sp, meta, id) => (object)new PrefabHandle<T>(owner, sp, meta, id)
        return Expression
            .Lambda<Func<PrefabModel, PrefabComponentMetadata, string?, object>>(
                newExpr, ownerParam, metaParam, idParam)
            .Compile();
    }

    private static Func<IReadOnlyCollection<IVaultModel>, Task>
     CompileSaveBatchDelegate(Type componentType)
    {
        // IVault<TComponent>
        var vaultType = typeof(IVault<>).MakeGenericType(componentType);

        // Enumerable.Cast<TComponent>(...)
        var castMethod = typeof(Enumerable)
            .GetMethod(nameof(Enumerable.Cast), BindingFlags.Public | BindingFlags.Static)!
            .MakeGenericMethod(componentType);

        // Enumerable.ToList<TComponent>(...)
        var toListMethod = typeof(Enumerable)
            .GetMethod(nameof(Enumerable.ToList), BindingFlags.Public | BindingFlags.Static)!
            .MakeGenericMethod(componentType);

        // Find SaveBatchAsync on IVault<TComponent>
        var enumerableOfComponent = typeof(IEnumerable<>).MakeGenericType(componentType);

        var saveBatch = vaultType.GetMethod(
                            nameof(IVault<IVaultModel>.SaveBatchAsync),
                            new[] { enumerableOfComponent, typeof(bool?) })
                      ?? vaultType.GetMethod(
                            nameof(IVault<IVaultModel>.SaveBatchAsync),
                            new[] { enumerableOfComponent })
                      ?? throw new InvalidOperationException(
                            $"IVault<{componentType.Name}> must have SaveBatchAsync.");

        // Build the delegate:
        // async items => {
        //   var vault = (IVault<TComponent>)Dependencies.Inject(vaultType);
        //   var typed = items.Cast<TComponent>().ToList();
        //   await vault.SaveBatchAsync(typed, null_or_default);
        // }
        return async items =>
        {
            if (items is null || items.Count == 0)
                return;

            // Resolve the vault via your global DI helper
            var vault = Dependencies.Inject(vaultType);

            // items : IReadOnlyCollection<IVaultModel> -> IEnumerable<IVaultModel> for Cast<T>
            var casted = castMethod.Invoke(null, new object[] { items })!;
            var list = toListMethod.Invoke(null, new object[] { casted })!;

            object? result;
            var parameters = saveBatch.GetParameters();

            if (parameters.Length == 2)
            {
                // SaveBatchAsync(IEnumerable<T>, bool?)
                result = saveBatch.Invoke(vault, new object[] { list, null });
            }
            else
            {
                // SaveBatchAsync(IEnumerable<T>)
                result = saveBatch.Invoke(vault, new object[] { list });
            }

            if (result is Task t)
            {
                await t.ConfigureAwait(false);
            }
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
    public Type ComponentType { get; init; } = default!;  // Spaceship, AnotherPrefab, etc.

    public Action<object, object?> Setter { get; init; } = default!;
    public Func<object, object?> Getter { get; init; } = default!;

    public Func<PrefabModel, PrefabComponentMetadata, string?, object> HandleFactory { get; init; } = default!;

    public Func<IReadOnlyCollection<IVaultModel>, Task> SaveBatchAsync { get; init; } = default!;

    /// <summary>
    /// If set, this component will be auto-loaded when the component with this name is loaded.
    /// </summary>
    public string? AutoLoadOn { get; init; }

    /// <summary>
    /// Relation key metadata (currently validated when AutoLoadOn is present).
    /// </summary>
    public string? RelationKey { get; init; }

    /// <summary>
    /// Compiled callbacks to invoke when this component is loaded.
    /// Signature: (prefabInstance, componentInstance, services) => Task
    /// </summary>
    public Func<object, object?, Task>[] OnLoadedCallbacks { get; init; }
        = Array.Empty<Func<object, object?, Task>>();
}
