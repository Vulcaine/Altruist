/*
Copyright 2025 Aron Gere
Licensed under the Apache License, Version 2.0
*/

using System.Linq.Expressions;
using System.Reflection;

namespace Altruist.Gaming;

/// <summary>
/// Composes a <see cref="StateMachineDef{TContext}"/> from attribute-decorated
/// handler classes, programmatic overrides, or both. The framework loads
/// nothing by itself — data sources (JSON, DB, config) stay in consumer code.
///
/// <para>Typical flow:</para>
/// <code>
/// var b = new StateMachineBuilder&lt;IComboContext&gt;();
/// b.RegisterHandlers&lt;WarriorUnarmedCombo&gt;();           // scan [State]/[StateWindow]/etc.
/// b.ConfigureState("attack1")
///  .Duration(1.0f)                                      // data-driven override
///  .Window("damage", 0.45f, 0.60f)
///  .Data(stepDef);
/// b.OnStateEnter((ctx, _, baseDur) =&gt;                  // per-domain transition hook
///     ctx.StateDuration = baseDur / Math.Max(0.5f, ((IComboContext)ctx).AttackSpeedMul));
/// b.SetInitial("entry");
/// StateMachineDef&lt;IComboContext&gt; def = b.Build();
/// </code>
/// </summary>
public sealed class StateMachineBuilder<TContext> where TContext : class, IStateContextCore
{
    private readonly Dictionary<string, Func<TContext, float, string?>> _updates = new();
    private readonly Dictionary<string, Action<TContext>> _enters = new();
    private readonly Dictionary<string, Action<TContext>> _exits = new();
    private readonly Dictionary<string, float> _delays = new();
    private readonly Dictionary<string, float> _durations = new();
    private readonly Dictionary<string, string?> _tags = new();
    private readonly Dictionary<string, Dictionary<string, StateWindow>> _windows = new();
    private readonly Dictionary<string, object?> _data = new();
    private readonly Dictionary<string, StateMotionProfile> _motions = new();
    private string? _initial;
    private Action<TContext, string, float>? _onStateEnter;

    /// <summary>Scan the class for <see cref="StateAttribute"/> / <see cref="StateEnterAttribute"/> /
    /// <see cref="StateExitAttribute"/> / <see cref="StateWindowAttribute"/> methods (and subclasses
    /// thereof, e.g. <c>[AIState]</c>, <c>[ComboState]</c>, <c>[DamageWindow]</c>). Instance created
    /// via <paramref name="instance"/> or <c>Activator.CreateInstance</c>.</summary>
    public StateMachineBuilder<TContext> RegisterHandlers<T>(T? instance = null) where T : class
    {
        var obj = instance ?? (T?)Activator.CreateInstance(typeof(T))
            ?? throw new InvalidOperationException($"Cannot instantiate {typeof(T).FullName}");
        return RegisterHandlers(typeof(T), obj);
    }

    /// <summary>Register handlers from an existing instance. Type is inferred via runtime type.</summary>
    public StateMachineBuilder<TContext> RegisterHandlers(object instance)
    {
        if (instance == null) throw new ArgumentNullException(nameof(instance));
        return RegisterHandlers(instance.GetType(), instance);
    }

    /// <summary>Non-generic overload: register handlers from a class given its Type and an instance.</summary>
    public StateMachineBuilder<TContext> RegisterHandlers(Type type, object instance)
    {
        if (type == null) throw new ArgumentNullException(nameof(type));
        if (instance == null) throw new ArgumentNullException(nameof(instance));

        // All state-related attributes are AllowMultiple = true (one handler method can back many
        // states, one state can have many windows). Iterate via GetCustomAttributes<T> (plural) so
        // repeats don't throw and we register each occurrence.
        const BindingFlags Flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        string? initialOfType = null;
        foreach (var method in type.GetMethods(Flags))
        {
            // [State] — update handlers. Subclass attributes (AIState, ComboState) match.
            var stateAttrs = method.GetCustomAttributes<StateAttribute>(inherit: false).ToArray();
            if (stateAttrs.Length > 0)
            {
                ValidateUpdateSignature(method, type);
                var ctxParamType = method.GetParameters()[0].ParameterType;
                var invoker = BuildUpdateInvoker(instance, method, ctxParamType);

                foreach (var attr in stateAttrs)
                {
                    _updates[attr.Name] = invoker;

                    if (!_tags.ContainsKey(attr.Name))
                        _tags[attr.Name] = attr.Tag;

                    if (attr.Delay > 0)
                        _delays[attr.Name] = attr.DelayUnit == TimeUnit.Milliseconds ? attr.Delay / 1000f : attr.Delay;

                    if (attr.Initial)
                    {
                        // Reflection returns methods in no defined order, so two initial states
                        // of one class would make the winner arbitrary.
                        if (initialOfType != null && initialOfType != attr.Name)
                            throw new InvalidOperationException(
                                $"{type.Name} marks both '{initialOfType}' and '{attr.Name}' as the initial state; mark exactly one.");
                        initialOfType = attr.Name;
                        _initial = attr.Name;
                    }
                }
            }

            // [StateEnter]
            foreach (var enterAttr in method.GetCustomAttributes<StateEnterAttribute>(inherit: false))
            {
                ValidateLifecycleSignature(method, type, nameof(StateEnterAttribute));
                _enters[enterAttr.StateName] = BuildLifecycleInvoker(instance, method, method.GetParameters()[0].ParameterType);
            }

            // [StateExit]
            foreach (var exitAttr in method.GetCustomAttributes<StateExitAttribute>(inherit: false))
            {
                ValidateLifecycleSignature(method, type, nameof(StateExitAttribute));
                _exits[exitAttr.StateName] = BuildLifecycleInvoker(instance, method, method.GetParameters()[0].ParameterType);
            }

            // [StateWindow] — many per state.
            foreach (var win in method.GetCustomAttributes<StateWindowAttribute>(inherit: false))
            {
                if (!_windows.TryGetValue(win.StateName, out var bucket))
                {
                    bucket = new Dictionary<string, StateWindow>();
                    _windows[win.StateName] = bucket;
                }
                bucket[win.Kind] = new StateWindow(win.Start, win.End);
            }
        }

        return this;
    }

    /// <summary>Begin configuring a specific state (fluent overrides). Overrides attribute defaults.</summary>
    public StateConfig ConfigureState(string name) => new StateConfig(this, name);

    /// <summary>Explicitly declare the initial state. Otherwise the <c>[State(Initial = true)]</c> of the last
    /// registered handler class wins; a machine with several states and no declared initial state does not build.</summary>
    public StateMachineBuilder<TContext> SetInitial(string name)
    {
        _initial = name;
        return this;
    }

    /// <summary>Register a transition hook fired on every state entry as
    /// <c>(context, stateName, baseDurationFromBuilder)</c>. Called BEFORE the per-state
    /// <c>[StateEnter]</c> method. Typical use: scale <c>context.StateDuration</c> by a
    /// per-entity multiplier (e.g. combo attack speed).</summary>
    public StateMachineBuilder<TContext> OnStateEnter(Action<TContext, string, float> hook)
    {
        _onStateEnter = hook;
        return this;
    }

    /// <summary>Build an immutable <see cref="StateMachineDef{TContext}"/>. The builder may be reused
    /// after Build but each Build produces an independent snapshot.</summary>
    /// <exception cref="InvalidOperationException">No states, no initial state declared while there are several
    /// (<c>[State(Initial = true)]</c>, <see cref="SetInitial"/> or <see cref="StateConfig.AsInitial"/>), or the initial
    /// state has no update handler.</exception>
    public StateMachineDef<TContext> Build()
    {
        if (_updates.Count == 0)
            throw new InvalidOperationException("StateMachineBuilder has no states registered.");

        var initial = _initial ?? SoleState();
        if (!_updates.ContainsKey(initial))
            throw new InvalidOperationException($"Initial state '{initial}' has no registered update handler.");

        // Convert inner window dictionaries to IReadOnlyDictionary<string, StateWindow>.
        var windowsReadonly = new Dictionary<string, IReadOnlyDictionary<string, StateWindow>>(_windows.Count);
        foreach (var kv in _windows)
            windowsReadonly[kv.Key] = kv.Value;

        return new StateMachineDef<TContext>(
            initial,
            new Dictionary<string, Func<TContext, float, string?>>(_updates),
            new Dictionary<string, Action<TContext>>(_enters),
            new Dictionary<string, Action<TContext>>(_exits),
            new Dictionary<string, float>(_delays),
            new Dictionary<string, float>(_durations),
            new Dictionary<string, string?>(_tags),
            windowsReadonly,
            new Dictionary<string, object?>(_data),
            _motions.ToDictionary(kv => kv.Key, kv => kv.Value.Clone()),
            _onStateEnter);
    }

    private string SoleState()
    {
        if (_updates.Count > 1)
            throw new InvalidOperationException(
                $"No initial state declared among [{string.Join(", ", _updates.Keys)}]; mark one with Initial = true, SetInitial or AsInitial.");
        return _updates.Keys.Single();
    }

    // ── Fluent per-state configurator ──────────────────────────────────────────

    /// <summary>Fluent per-state overrides returned by <see cref="ConfigureState"/>; every call
    /// writes into the owning builder (taking effect at the next <see cref="Build"/>).</summary>
    public sealed class StateConfig
    {
        private readonly StateMachineBuilder<TContext> _owner;
        private readonly string _name;

        internal StateConfig(StateMachineBuilder<TContext> owner, string name)
        {
            _owner = owner;
            _name = name;
        }

        /// <summary>Base duration in seconds. 0 = open-ended (no <see cref="IStateContextCore.Progress"/>).</summary>
        public StateConfig Duration(float seconds)
        {
            _owner._durations[_name] = seconds;
            return this;
        }

        /// <summary>Delay before the Update handler starts running after entry. Enter/Exit still fire immediately.</summary>
        public StateConfig Delay(float seconds)
        {
            _owner._delays[_name] = seconds;
            return this;
        }

        /// <summary>Overwrite the state's tag string, readable via <see cref="IStateContextCore.CurrentStateTag"/>.</summary>
        public StateConfig Tag(string tag)
        {
            _owner._tags[_name] = tag;
            return this;
        }

        /// <summary>Add or override a named window (normalized [start, end]) for this state.</summary>
        public StateConfig Window(string kind, float start, float end)
        {
            if (!_owner._windows.TryGetValue(_name, out var bucket))
            {
                bucket = new Dictionary<string, StateWindow>();
                _owner._windows[_name] = bucket;
            }
            bucket[kind] = new StateWindow(start, end);
            return this;
        }

        /// <summary>Attach an opaque user data blob retrievable later via <c>def.GetData(name)</c>.</summary>
        public StateConfig Data(object blob)
        {
            _owner._data[_name] = blob;
            return this;
        }

        /// <summary>Attach a first-class motion profile to the state.</summary>
        public StateConfig Motion(StateMotionProfile motion)
        {
            _owner._motions[_name] = motion?.Clone() ?? StateMotionProfile.Default();
            return this;
        }

        /// <summary>Convenience override for the state's locomotion damping / throttle.</summary>
        public StateConfig MotionThrottle(float movementThrottle)
        {
            var motion = _owner._motions.TryGetValue(_name, out var existing)
                ? existing.Clone()
                : StateMotionProfile.Default();
            motion.MovementThrottle = Math.Clamp(movementThrottle, 0f, 1f);
            _owner._motions[_name] = motion;
            return this;
        }

        /// <summary>Register a raw update handler (no attribute) for this state.</summary>
        public StateConfig Handler(Func<TContext, float, string?> update)
        {
            _owner._updates[_name] = update;
            return this;
        }

        /// <summary>Register a raw enter hook (no attribute) for this state.</summary>
        public StateConfig OnEnter(Action<TContext> enter)
        {
            _owner._enters[_name] = enter;
            return this;
        }

        /// <summary>Register a raw exit hook (no attribute) for this state.</summary>
        public StateConfig OnExit(Action<TContext> exit)
        {
            _owner._exits[_name] = exit;
            return this;
        }

        /// <summary>Mark this state as the initial state. Overrides any <c>[State(Initial = true)]</c> in handlers.</summary>
        public StateConfig AsInitial()
        {
            _owner._initial = _name;
            return this;
        }
    }

    // ── Validation + invoker compilation (expression trees, same shape as AIBehaviorDiscovery) ──

    private static void ValidateUpdateSignature(MethodInfo method, Type declaringType)
    {
        var pars = method.GetParameters();
        if (pars.Length != 2
            || !typeof(IStateContextCore).IsAssignableFrom(pars[0].ParameterType)
            || pars[1].ParameterType != typeof(float))
        {
            throw new InvalidOperationException(
                $"[State] method '{declaringType.Name}.{method.Name}' must have signature (TContext : IStateContextCore, float).");
        }
        if (method.ReturnType != typeof(string))
        {
            throw new InvalidOperationException(
                $"[State] method '{declaringType.Name}.{method.Name}' must return string (nullable allowed). Found {method.ReturnType}.");
        }
    }

    private static void ValidateLifecycleSignature(MethodInfo method, Type declaringType, string attrName)
    {
        var pars = method.GetParameters();
        if (pars.Length != 1 || !typeof(IStateContextCore).IsAssignableFrom(pars[0].ParameterType))
        {
            throw new InvalidOperationException(
                $"[{attrName}] method '{declaringType.Name}.{method.Name}' must have signature (TContext : IStateContextCore).");
        }
        if (method.ReturnType != typeof(void))
        {
            throw new InvalidOperationException(
                $"[{attrName}] method '{declaringType.Name}.{method.Name}' must return void. Found {method.ReturnType}.");
        }
    }

    private static Func<TContext, float, string?> BuildUpdateInvoker(object target, MethodInfo method, Type contextType)
    {
        var targetConst = Expression.Constant(target);
        var ctxParam = Expression.Parameter(typeof(TContext), "ctx");
        var dtParam = Expression.Parameter(typeof(float), "dt");
        var castCtx = Expression.Convert(ctxParam, contextType);
        var call = Expression.Call(targetConst, method, castCtx, dtParam);
        var lambda = Expression.Lambda<Func<TContext, float, string?>>(call, ctxParam, dtParam);
        return lambda.Compile();
    }

    private static Action<TContext> BuildLifecycleInvoker(object target, MethodInfo method, Type contextType)
    {
        var targetConst = Expression.Constant(target);
        var ctxParam = Expression.Parameter(typeof(TContext), "ctx");
        var castCtx = Expression.Convert(ctxParam, contextType);
        var call = Expression.Call(targetConst, method, castCtx);
        var lambda = Expression.Lambda<Action<TContext>>(call, ctxParam);
        return lambda.Compile();
    }
}
