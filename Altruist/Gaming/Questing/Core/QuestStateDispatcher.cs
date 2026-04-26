using System.Reflection;

namespace Altruist.Gaming.Questing;

public sealed class QuestStateDispatcher<TContext> where TContext : QuestContext
{
    private const string CurrentStateKey = "__state";
    private readonly Dictionary<string, Func<TContext, Task<string?>>> _states;
    private readonly Dictionary<string, Func<TContext, Task>> _enters;
    private readonly Dictionary<string, Func<TContext, Task>> _exits;
    private readonly string _initialState;

    private QuestStateDispatcher(
        Dictionary<string, Func<TContext, Task<string?>>> states,
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

    public static QuestStateDispatcher<TContext> Compile(Type type, object instance)
    {
        const BindingFlags Flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        var states = new Dictionary<string, Func<TContext, Task<string?>>>(StringComparer.Ordinal);
        var enters = new Dictionary<string, Func<TContext, Task>>(StringComparer.Ordinal);
        var exits = new Dictionary<string, Func<TContext, Task>>(StringComparer.Ordinal);
        string? initial = null;

        foreach (var method in type.GetMethods(Flags))
        {
            foreach (var attr in method.GetCustomAttributes<QuestStateAttribute>(false))
            {
                states[attr.Name] = BuildStateInvoker(instance, method);
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

    public async Task<bool> DispatchAsync(TContext context)
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
        if (!_states.TryGetValue(current, out var handler))
            return false;

        var next = await handler(context);
        if (!string.IsNullOrWhiteSpace(next) && next != current && _states.ContainsKey(next))
        {
            if (_exits.TryGetValue(current, out var exit))
                await exit(context);

            context.State.Set(CurrentStateKey, next);

            if (_enters.TryGetValue(next, out var enter))
                await enter(context);
        }

        return true;
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
}
