using System.Reflection;

namespace Altruist.Gaming.Questing;

public sealed class QuestStateDispatcher<TContext> where TContext : QuestContext
{
    private const string CurrentStateKey = "__state";
    private const string DoneKey = "_done";
    private const string WildcardHookKey = "*";
    private readonly Dictionary<string, Dictionary<string, Func<TContext, Task<string?>>>> _states;
    private readonly Dictionary<string, Func<TContext, Task>> _enters;
    private readonly Dictionary<string, Func<TContext, Task>> _exits;
    private readonly string _initialState;

    private QuestStateDispatcher(
        Dictionary<string, Dictionary<string, Func<TContext, Task<string?>>>> states,
        Dictionary<string, Func<TContext, Task>> enters,
        Dictionary<string, Func<TContext, Task>> exits,
        string initialState)
    {
        _states = states;
        _enters = enters;
        _exits = exits;
        _initialState = initialState;
    }

    public bool HasStateHandlers => _states.Count > 0;
    public bool HasGenericStateHandlers => _states.Values.Any(handlers => handlers.ContainsKey(WildcardHookKey));

    public bool HasHook(string hookKey)
    {
        if (string.IsNullOrWhiteSpace(hookKey))
            return false;

        return _states.Values.Any(handlers => handlers.ContainsKey(hookKey));
    }

    public static QuestStateDispatcher<TContext> Compile(Type type, object instance)
    {
        const BindingFlags Flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        var states = new Dictionary<string, Dictionary<string, Func<TContext, Task<string?>>>>(StringComparer.Ordinal);
        var enters = new Dictionary<string, Func<TContext, Task>>(StringComparer.Ordinal);
        var exits = new Dictionary<string, Func<TContext, Task>>(StringComparer.Ordinal);
        string? initial = null;

        foreach (var method in type.GetMethods(Flags))
        {
            foreach (var attr in method.GetCustomAttributes<QuestStateAttribute>(false))
            {
                if (!states.TryGetValue(attr.Name, out var handlers))
                {
                    handlers = new Dictionary<string, Func<TContext, Task<string?>>>(StringComparer.OrdinalIgnoreCase);
                    states[attr.Name] = handlers;
                }

                handlers[ResolveHookKey(method.Name)] = BuildStateInvoker(instance, method);
                if (attr.Initial)
                    initial = attr.Name;
            }

            foreach (var attr in method.GetCustomAttributes<QuestStateEnterAttribute>(false))
                enters[attr.StateName] = BuildLifecycleInvoker(instance, method);

            foreach (var attr in method.GetCustomAttributes<QuestStateExitAttribute>(false))
                exits[attr.StateName] = BuildLifecycleInvoker(instance, method);
        }

        initial ??= states.Keys.FirstOrDefault() ?? "";
        return new QuestStateDispatcher<TContext>(states, enters, exits, initial);
    }

    public async Task<bool> DispatchAsync(TContext context, string hookKey)
    {
        if (_states.Count == 0)
            return false;

        if (string.IsNullOrWhiteSpace(context.State.CurrentState))
        {
            context.State.CurrentState = _initialState;
            if (_enters.TryGetValue(_initialState, out var initialEnter))
                await initialEnter(context);
        }

        var current = context.State.CurrentState;
        if (!_states.TryGetValue(current, out var handlers))
            return false;

        if (!handlers.TryGetValue(hookKey, out var handler)
            && !handlers.TryGetValue(WildcardHookKey, out handler))
            return false;

        var next = await handler(context);
        await ApplyTransitionAsync(context, current, next);
        return true;
    }

    private async Task ApplyTransitionAsync(TContext context, string current, string? next)
    {
        if (string.IsNullOrWhiteSpace(next) || next == current)
            return;

        if (_exits.TryGetValue(current, out var exit))
            await exit(context);

        context.State.Set(CurrentStateKey, next);

        if (_states.ContainsKey(next))
        {
            if (_enters.TryGetValue(next, out var enter))
                await enter(context);
            return;
        }

        context.State.Set(DoneKey, true);
    }

    private static Func<TContext, Task<string?>> BuildStateInvoker(object target, MethodInfo method)
    {
        ValidateContextOnly(method);

        return async ctx =>
        {
            var result = method.Invoke(target, new object[] { ctx });
            return result switch
            {
                Task<string?> task => await task,
                ValueTask<string?> task => await task,
                string next => next,
                null => null,
                _ => throw new InvalidOperationException($"Quest state method {method.Name} must return string? or Task<string?>."),
            };
        };
    }

    private static Func<TContext, Task> BuildLifecycleInvoker(object target, MethodInfo method)
    {
        ValidateContextOnly(method);

        return async ctx =>
        {
            var result = method.Invoke(target, new object[] { ctx });
            switch (result)
            {
                case Task task:
                    await task;
                    break;
                case ValueTask task:
                    await task;
                    break;
                case null:
                    break;
                default:
                    throw new InvalidOperationException($"Quest state lifecycle method {method.Name} must return void, Task, or ValueTask.");
            }
        };
    }

    private static void ValidateContextOnly(MethodInfo method)
    {
        var parameters = method.GetParameters();
        if (parameters.Length != 1 || !parameters[0].ParameterType.IsAssignableFrom(typeof(TContext)))
        {
            throw new InvalidOperationException(
                $"Quest state method {method.DeclaringType?.Name}.{method.Name} must accept one {typeof(TContext).Name} parameter.");
        }
    }

    private static string ResolveHookKey(string methodName)
    {
        return methodName switch
        {
            nameof(IOnEnter<TContext>.OnEnter) => QuestHooks.Enter,
            nameof(IOnLeave<TContext>.OnLeave) => QuestHooks.Leave,
            nameof(IOnLevel<TContext>.OnLevel) => QuestHooks.Level,
            nameof(IOnKill<TContext>.OnKill) => QuestHooks.Kill,
            nameof(IOnNpc<TContext>.OnNpc) => QuestHooks.Npc,
            nameof(IOnItem<TContext>.OnItem) => QuestHooks.Item,
            nameof(IOnButton<TContext>.OnButton) => QuestHooks.Button,
            nameof(IOnTimer<TContext>.OnTimer) => QuestHooks.Timer,
            _ when methodName.Length > 2 && methodName.StartsWith("On", StringComparison.Ordinal) =>
                ResolveCustomOrPrefixedHookKey(methodName),
            _ => WildcardHookKey,
        };
    }

    private static string ResolveCustomOrPrefixedHookKey(string methodName)
    {
        if (methodName.StartsWith(nameof(IOnEnter<TContext>.OnEnter), StringComparison.Ordinal))
            return QuestHooks.Enter;
        if (methodName.StartsWith(nameof(IOnLeave<TContext>.OnLeave), StringComparison.Ordinal))
            return QuestHooks.Leave;
        if (methodName.StartsWith(nameof(IOnLevel<TContext>.OnLevel), StringComparison.Ordinal))
            return QuestHooks.Level;
        if (methodName.StartsWith(nameof(IOnKill<TContext>.OnKill), StringComparison.Ordinal))
            return QuestHooks.Kill;
        if (methodName.StartsWith(nameof(IOnNpc<TContext>.OnNpc), StringComparison.Ordinal))
            return QuestHooks.Npc;
        if (methodName.StartsWith(nameof(IOnItem<TContext>.OnItem), StringComparison.Ordinal))
            return QuestHooks.Item;
        if (methodName.StartsWith(nameof(IOnButton<TContext>.OnButton), StringComparison.Ordinal))
            return QuestHooks.Button;
        if (methodName.StartsWith(nameof(IOnTimer<TContext>.OnTimer), StringComparison.Ordinal))
            return QuestHooks.Timer;

        return char.ToLowerInvariant(methodName[2]) + methodName[3..];
    }
}
