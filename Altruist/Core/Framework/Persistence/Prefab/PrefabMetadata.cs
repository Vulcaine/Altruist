using System.Collections.Concurrent;
using System.Reflection;
using System.Linq.Expressions;
using Microsoft.Extensions.DependencyInjection;

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
                OnLoadedCallbacks = Array.Empty<Func<object, object?, IServiceProvider, Task>>()
            });
        }

        var metas = list.ToArray();

        // ---------- scan methods for [OnPrefabComponentLoad] ----------
        var callbacksByComponent = new Dictionary<string, List<Func<object, object?, IServiceProvider, Task>>>(
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
                    listForComponent = new List<Func<object, object?, IServiceProvider, Task>>();
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
                    ?? Array.Empty<Func<object, object?, IServiceProvider, Task>>()
            });
        }

        _byPrefab[prefabType] = finalList.ToArray();
    }

    private static Func<object, object?, IServiceProvider, Task> CompileOnComponentLoadCallback(
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
        return async (prefabObj, componentObj, services) =>
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
                    args[i] = services.GetRequiredService(serviceType);
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

    private static Func<PrefabModel, IServiceProvider, PrefabComponentMetadata, string?, object>
        CompileHandleFactory(Type componentType)
    {
        var handleClr = typeof(PrefabHandle<>).MakeGenericType(componentType);

        var ctor = handleClr.GetConstructor(new[]
        {
            typeof(PrefabModel),
            typeof(IServiceProvider),
            typeof(PrefabComponentMetadata),
            typeof(string)
        });

        if (ctor is null)
            throw new InvalidOperationException(
                $"PrefabHandle<{componentType.Name}> must have ctor(PrefabModel, IServiceProvider, PrefabComponentMetadata, string).");

        var ownerParam = Expression.Parameter(typeof(PrefabModel), "owner");
        var spParam = Expression.Parameter(typeof(IServiceProvider), "sp");
        var metaParam = Expression.Parameter(typeof(PrefabComponentMetadata), "meta");
        var idParam = Expression.Parameter(typeof(string), "id");

        var newExpr = Expression.New(ctor, ownerParam, spParam, metaParam, idParam);

        // (owner, sp, meta, id) => (object)new PrefabHandle<T>(owner, sp, meta, id)
        return Expression
            .Lambda<Func<PrefabModel, IServiceProvider, PrefabComponentMetadata, string?, object>>(
                newExpr, ownerParam, spParam, metaParam, idParam)
            .Compile();
    }

    private static Func<IServiceProvider, IReadOnlyCollection<IVaultModel>, Task>
        CompileSaveBatchDelegate(Type componentType)
    {
        // We’re building:
        // async (sp, items) => {
        //   var vault = sp.GetRequiredService<IVault<TComponent>>();
        //   var typed = items.Cast<TComponent>().ToList();
        //   await vault.SaveBatchAsync(typed, null);
        // }

        var spParam = Expression.Parameter(typeof(IServiceProvider), "sp");
        var itemsParam = Expression.Parameter(typeof(IReadOnlyCollection<IVaultModel>), "items");

        var vaultType = typeof(IVault<>).MakeGenericType(componentType);
        var getReqGeneric = typeof(ServiceProviderServiceExtensions)
            .GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Single(m => m.Name == nameof(ServiceProviderServiceExtensions.GetRequiredService)
                      && m.IsGenericMethodDefinition
                      && m.GetParameters().Length == 1)
            .MakeGenericMethod(vaultType);

        var castMethod = typeof(Enumerable)
            .GetMethod(nameof(Enumerable.Cast), BindingFlags.Public | BindingFlags.Static)!
            .MakeGenericMethod(componentType);

        var toListMethod = typeof(Enumerable)
            .GetMethod(nameof(Enumerable.ToList), BindingFlags.Public | BindingFlags.Static)!
            .MakeGenericMethod(componentType);

        var saveBatch = vaultType.GetMethod(nameof(IVault<IVaultModel>.SaveBatchAsync),
            new[] { typeof(IEnumerable<>).MakeGenericType(componentType), typeof(bool?) })
                            ?? vaultType.GetMethod(nameof(IVault<IVaultModel>.SaveBatchAsync),
                                new[] { typeof(IEnumerable<>).MakeGenericType(componentType) });

        if (saveBatch is null)
            throw new InvalidOperationException($"IVault<{componentType.Name}> must have SaveBatchAsync.");

        // vault = sp.GetRequiredService<IVault<TComponent>>()
        var vaultExpr = Expression.Call(getReqGeneric, spParam);

        // (IReadOnlyCollection<IVaultModel>) -> IEnumerable<IVaultModel> for Cast<T>
        var itemsAsEnumerable = Expression.Convert(itemsParam, typeof(IEnumerable<IVaultModel>));

        var casted = Expression.Call(castMethod, itemsAsEnumerable);
        var list = Expression.Call(toListMethod, casted);

        // vault.SaveBatchAsync(list, null)
        var saveCall = saveBatch.GetParameters().Length == 2
            ? Expression.Call(vaultExpr, saveBatch, list, Expression.Constant(null, typeof(bool?)))
            : Expression.Call(vaultExpr, saveBatch, list);

        var lambda = Expression.Lambda<Func<IServiceProvider, IReadOnlyCollection<IVaultModel>, Task>>(
            saveCall, spParam, itemsParam);

        return lambda.Compile();
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

    public Func<PrefabModel, IServiceProvider, PrefabComponentMetadata, string?, object> HandleFactory { get; init; } = default!;

    public Func<IServiceProvider, IReadOnlyCollection<IVaultModel>, Task> SaveBatchAsync { get; init; } = default!;

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
    public Func<object, object?, IServiceProvider, Task>[] OnLoadedCallbacks { get; init; }
        = Array.Empty<Func<object, object?, IServiceProvider, Task>>();
}
