using System.Reflection;

namespace Altruist.Gaming.Questing;

/// <summary>
/// Compiled state machine of a quest behavior, built from its <see cref="QuestStateAttribute"/>,
/// <see cref="QuestStateEnterAttribute"/> and <see cref="QuestStateExitAttribute"/> methods.
/// Created by the runtime / <see cref="QuestDefinition{T}.Create"/>; game code rarely uses it
/// directly. The current state lives in the subject's <see cref="IQuestState"/>, so one
/// dispatcher serves every subject.
/// </summary>
/// <typeparam name="TContext">The game's quest context type.</typeparam>
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

    /// <summary>True when the behavior declares at least one <see cref="QuestStateAttribute"/> state.</summary>
    public bool HasStateHandlers => _states.Count > 0;
    /// <summary>True when any state has a wildcard handler (a state method whose name does not start with <c>On</c>).</summary>
    public bool HasGenericStateHandlers => _states.Values.Any(handlers => handlers.ContainsKey(WildcardHookKey));

    /// <summary>True when any state (not necessarily the current one) has a specific handler for <paramref name="hookKey"/>.</summary>
    /// <param name="hookKey">Hook key.</param>
    /// <returns>Whether some state handles the hook.</returns>
    public bool HasHook(string hookKey)
    {
        if (string.IsNullOrWhiteSpace(hookKey))
            return false;

        return _states.Values.Any(handlers => handlers.ContainsKey(hookKey));
    }

    /// <summary>
    /// Reflects over the instance methods (public and non-public; inherited private methods are not seen)
    /// of <paramref name="type"/> and builds the state table. See <see cref="QuestStateAttribute"/>
    /// for naming and signature rules.
    /// </summary>
    /// <param name="type">The behavior type to scan.</param>
    /// <param name="instance">The behavior instance the handlers are invoked on.</param>
    /// <returns>The dispatcher (empty when no states are declared).</returns>
    /// <exception cref="InvalidOperationException">A state or lifecycle method does not take exactly one parameter assignable from <typeparamref name="TContext"/>.</exception>
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

    /// <summary>
    /// Dispatches <paramref name="hookKey"/> to the current state. If no state is stored yet, the
    /// initial state is set and its enter handler runs first. The current state's specific handler
    /// (else its wildcard handler) runs, and its returned next state is applied: exit handler of the
    /// old state, store the new state, then its enter handler, or set <c>_done</c> when the new state
    /// is unknown.
    /// </summary>
    /// <param name="context">Dispatch context whose <see cref="QuestContext.State"/> holds the current state.</param>
    /// <param name="hookKey">Hook key.</param>
    /// <returns>True when a state handler ran; false when there are no states, the current state is unknown, or it has no matching handler (callers then fall back to hook-interface handlers).</returns>
    /// <exception cref="InvalidOperationException">A handler returned something other than <c>string?</c>, <c>Task&lt;string?&gt;</c> or <c>ValueTask&lt;string?&gt;</c>.</exception>
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
