/*
Copyright 2025 Aron Gere
Licensed under the Apache License, Version 2.0
*/

using System.Reflection;

using Box2DSharp.Dynamics;

namespace Altruist.Physx.TwoD
{
    /// <summary>
    /// Makes independent Box2D worlds safe to step on different threads at the same time.
    /// <para>
    /// Box2DSharp keeps one contact factory per shape pair in a static table, and each factory
    /// pools its contacts without locking: two worlds stepping at once on two threads corrupt
    /// the pools (crashes, or contacts shared between worlds). This replaces every factory in
    /// the table with a proxy that forwards to a factory of the same type owned by the calling
    /// thread, so each thread pools its own contacts. One world is still stepped by one thread
    /// at a time; a world may move between threads between steps (its contacts go back to
    /// whichever thread's pool destroys them, and every pooled contact is fully reset on reuse).
    /// </para>
    /// <para>
    /// Installed by the first <see cref="Box2DWorldEngine2D"/>; idempotent. On a single thread the
    /// pooling behaves exactly as before, so simulations stay bit-identical.
    /// </para>
    /// <para>
    /// Mechanism: reflection into Box2DSharp internals (<c>ContactManager._registers</c>,
    /// <c>ContactRegister</c>, <c>IContactFactory</c>); a Box2DSharp version with another layout makes
    /// the install fail, leaving the table untouched, <see cref="ParallelWorldsSupported"/> false and
    /// <see cref="FailureReason"/> set. In that case step worlds from a single thread (or serialize
    /// their steps). Each thread that steps a world keeps its own factory instances (thread-static);
    /// they are not released when the thread ends. What is NOT made thread-safe: a single world, its
    /// bodies and fixtures (one thread at a time), and the static collider/body provider facades.
    /// </para>
    /// </summary>
    public static class Box2DThreading
    {
        private static readonly object Gate = new();
        private static bool _attempted;

        /// <summary>The contact factories are per-thread: worlds may step in parallel.</summary>
        public static bool ParallelWorldsSupported { get; private set; }

        /// <summary>Why the install failed (a Box2DSharp version with a different layout), or null.</summary>
        public static string? FailureReason { get; private set; }

        /// <summary>Installs the per-thread contact factories once; returns <see cref="ParallelWorldsSupported"/>.</summary>
        public static bool EnsureInstalled()
        {
            if (_attempted)
                return ParallelWorldsSupported;
            lock (Gate)
            {
                if (_attempted)
                    return ParallelWorldsSupported;
                try
                {
                    Install();
                    ParallelWorldsSupported = true;
                }
                catch (Exception ex)
                {
                    FailureReason = ex.Message;
                    ParallelWorldsSupported = false;
                }
                _attempted = true;
                return ParallelWorldsSupported;
            }
        }

        private static void Install()
        {
            var asm = typeof(World).Assembly;
            var factoryInterface = asm.GetType("Box2DSharp.Dynamics.Contacts.IContactFactory")
                ?? throw new InvalidOperationException("Box2DSharp IContactFactory not found.");
            var registerType = asm.GetType("Box2DSharp.Dynamics.ContactRegister")
                ?? throw new InvalidOperationException("Box2DSharp ContactRegister not found.");
            var registersField = typeof(ContactManager).GetField("_registers", BindingFlags.Static | BindingFlags.NonPublic)
                ?? throw new InvalidOperationException("Box2DSharp ContactManager._registers not found.");
            var factoryField = registerType.GetField("Factory")
                ?? throw new InvalidOperationException("Box2DSharp ContactRegister.Factory not found.");
            var primaryField = registerType.GetField("Primary")
                ?? throw new InvalidOperationException("Box2DSharp ContactRegister.Primary not found.");
            var ctor = registerType.GetConstructor(new[] { factoryInterface, typeof(bool) })
                ?? throw new InvalidOperationException("Box2DSharp ContactRegister(IContactFactory, bool) not found.");

            if (registersField.GetValue(null) is not Array registers || registers.Rank != 2)
                throw new InvalidOperationException("Box2DSharp ContactManager._registers has an unexpected shape.");

            // Build every replacement first, then swap: a failure leaves the table untouched.
            var replacements = new List<(int I, int J, object Register)>();
            for (var i = 0; i < registers.GetLength(0); i++)
            {
                for (var j = 0; j < registers.GetLength(1); j++)
                {
                    var register = registers.GetValue(i, j);
                    if (register is null)
                        continue;
                    var factory = factoryField.GetValue(register)
                        ?? throw new InvalidOperationException("Box2DSharp contact register without a factory.");
                    if (factory is PerThreadContactFactory)
                        return; // already installed (another load context)
                    var proxy = DispatchProxy.Create(factoryInterface, typeof(PerThreadContactFactory));
                    ((PerThreadContactFactory)proxy).FactoryType = factory.GetType();
                    replacements.Add((i, j, ctor.Invoke(new[] { proxy, primaryField.GetValue(register)! })));
                }
            }
            foreach (var (i, j, register) in replacements)
                registers.SetValue(register, i, j);
        }
    }

    /// <summary>Forwards a Box2D contact factory's calls to the calling thread's own instance of it.
    /// Infrastructure for <see cref="Box2DThreading"/> (public only because <see cref="DispatchProxy"/>
    /// requires it); not meant to be used directly.</summary>
    public class PerThreadContactFactory : DispatchProxy
    {
        [ThreadStatic]
        private static Dictionary<Type, object>? _factories;

        internal Type FactoryType = null!;

        /// <summary>Invokes <paramref name="targetMethod"/> on the calling thread's factory of
        /// the proxied type (created on first use), rethrowing the inner exception unwrapped.</summary>
        /// <param name="targetMethod">The factory method being called.</param>
        /// <param name="args">Its arguments.</param>
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            var factories = _factories ??= new Dictionary<Type, object>();
            if (!factories.TryGetValue(FactoryType, out var factory))
                factories[FactoryType] = factory = Activator.CreateInstance(FactoryType, nonPublic: true)!;
            try
            {
                return targetMethod!.Invoke(factory, args);
            }
            catch (TargetInvocationException ex) when (ex.InnerException is not null)
            {
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
                throw;
            }
        }
    }
}
