using System.Linq.Expressions;
using System.Reflection;

using Microsoft.Extensions.Logging;

namespace Altruist.Physx
{
    /// <summary>
    /// Reflection helpers that turn <see cref="CollisionEventAttribute"/> methods into compiled invokers in
    /// <see cref="CollisionHandlerRegistry"/>. With the Altruist host this runs automatically
    /// (<see cref="AltruistCollisionHandlerConfig"/>); call it directly only in custom hosts or tests.
    /// </summary>
    public static class CollisionHandlerDiscovery
    {
        /// <summary>
        /// Scans <paramref name="assemblies"/> for <see cref="CollisionHandlerAttribute"/> classes, creates one instance
        /// of each and registers all their <see cref="CollisionEventAttribute"/> methods into <see cref="CollisionHandlerRegistry"/>.
        /// Does not clear the registry first.
        /// </summary>
        /// <param name="assemblies">Assemblies to scan.</param>
        /// <param name="instanceFactory">
        /// Resolver for handler instances, e.g. <c>type =&gt; serviceProvider.GetService(type)</c>. When it returns
        /// <see langword="null"/> the method falls back to <see cref="Activator.CreateInstance(Type)"/> (requires a public parameterless constructor).
        /// </param>
        /// <param name="logger">Logger for diagnostics.</param>
        /// <exception cref="ArgumentNullException">Any argument is <see langword="null"/>.</exception>
        /// <exception cref="InvalidOperationException">A handler method has an invalid signature (see <see cref="CollisionEventAttribute"/>).</exception>
        /// <example>
        /// <code>
        /// CollisionHandlerDiscovery.RegisterCollisionHandlers(
        ///     new[] { typeof(MyHandlers).Assembly }, type =&gt; provider.GetService(type), logger);
        /// </code>
        /// </example>
        public static void RegisterCollisionHandlers(
            IEnumerable<Assembly> assemblies,
            Func<Type, object?> instanceFactory,
            ILogger logger)
        {
            if (assemblies is null)
                throw new ArgumentNullException(nameof(assemblies));
            if (instanceFactory is null)
                throw new ArgumentNullException(nameof(instanceFactory));
            if (logger is null)
                throw new ArgumentNullException(nameof(logger));

            var handlerTypes = TypeDiscovery.FindTypesWithAttribute<CollisionHandlerAttribute>(assemblies);

            foreach (var handlerType in handlerTypes)
            {
                object? instance = instanceFactory(handlerType)
                                   ?? Activator.CreateInstance(handlerType);

                if (instance is null)
                {
                    logger.LogWarning("⚠️ Could not create instance of collision handler type {Type}. Skipping.",
                        handlerType.FullName);
                    continue;
                }

                RegisterCollisionMethodsFromInstance(instance, logger);
            }
        }

        /// <summary>
        /// Registers the <see cref="CollisionEventAttribute"/> methods of an explicit list of handler types (no assembly
        /// scan). Unlike <see cref="RegisterCollisionHandlers"/> there is no <see cref="Activator"/> fallback: types the
        /// factory cannot resolve are logged and skipped. Does not clear the registry first.
        /// </summary>
        /// <param name="handlerTypes">Handler classes to register.</param>
        /// <param name="instanceFactory">Resolver for handler instances, typically the DI root provider.</param>
        /// <param name="logger">Logger for diagnostics.</param>
        /// <exception cref="ArgumentNullException">Any argument is <see langword="null"/>.</exception>
        /// <exception cref="InvalidOperationException">A handler method has an invalid signature (see <see cref="CollisionEventAttribute"/>).</exception>
        public static void RegisterCollisionHandlerTypes(
            IEnumerable<Type> handlerTypes,
            Func<Type, object?> instanceFactory,
            ILogger logger)
        {
            if (handlerTypes is null)
                throw new ArgumentNullException(nameof(handlerTypes));
            if (instanceFactory is null)
                throw new ArgumentNullException(nameof(instanceFactory));
            if (logger is null)
                throw new ArgumentNullException(nameof(logger));

            foreach (var handlerType in handlerTypes)
            {
                object? instance = instanceFactory(handlerType);

                if (instance is null)
                {
                    logger.LogWarning("Could not resolve collision handler type {Type}. Skipping.",
                        handlerType.FullName);
                    continue;
                }

                RegisterCollisionMethodsFromInstance(instance, logger);
            }
        }

        /// <summary>
        /// Registers all [CollisionEvent] methods on a given handler instance.
        /// Follows the pattern of RegisterGateMethodsFromInstance, but
        /// specialized for collisions and using the shared TypeDiscovery
        /// helper to find methods.
        /// </summary>
        private static void RegisterCollisionMethodsFromInstance(object instance, ILogger log)
        {
            var type = instance.GetType();

            var methodsWithAttr =
                TypeDiscovery.FindInstanceMethodsWithAttribute<CollisionEventAttribute>(type);

            foreach (var (method, attr) in methodsWithAttr)
            {
                var pars = method.GetParameters();

                // Require event payload first, followed by the two entities/components.
                if (pars.Length != 3)
                    throw new InvalidOperationException(
                        $"Method {type.Name}.{method.Name} marked with [CollisionEvent] must have exactly 3 parameters: event payload, entity A, entity B.");

                var payloadType = pars[0].ParameterType;
                var paramA = pars[1].ParameterType;
                var paramB = pars[2].ParameterType;

                if (!payloadType.IsClass || payloadType.IsAbstract)
                    throw new InvalidOperationException(
                        $"First parameter of {type.Name}.{method.Name} must be a concrete event payload reference type.");

                if (!payloadType.IsAssignableFrom(attr.EventType) && !attr.EventType.IsAssignableFrom(payloadType))
                    throw new InvalidOperationException(
                        $"First parameter of {type.Name}.{method.Name} must be compatible with the [CollisionEvent] event type.");

                if (!paramA.IsClass || paramA.IsAbstract)
                    throw new InvalidOperationException(
                        $"Second parameter of {type.Name}.{method.Name} must be a concrete reference type.");

                if (!paramB.IsClass || paramB.IsAbstract)
                    throw new InvalidOperationException(
                        $"Third parameter of {type.Name}.{method.Name} must be a concrete reference type.");

                if (method.ReturnType != typeof(void))
                    throw new InvalidOperationException(
                        $"Method {type.Name}.{method.Name} marked with [CollisionEvent] must return void.");

                // Build a compiled delegate Action<object?, object, object> that:
                //   - casts the event payload first
                //   - casts the two objects to the method's parameter types
                //   - calls the method on the given instance.
                var invoker = BuildInvoker(instance, method, paramA, paramB, payloadType);

                var descriptor = new CollisionHandlerRegistry.HandlerDescriptor(
                    HandlerType: type,
                    ParamTypeA: paramA,
                    ParamTypeB: paramB,
                    EventType: attr.EventType,
                    Invoker: invoker);

                CollisionHandlerRegistry.Register(descriptor);

                log.LogDebug(
                    "✅ Registered collision handler {Handler}.{Method} for ({ParamA}, {ParamB}) with event {EventType}.",
                    type.FullName,
                    method.Name,
                    paramA.FullName,
                    paramB.FullName,
                    attr.EventType.FullName);
            }
        }

        /// <summary>
        /// Builds an Action&lt;object?, object, object&gt; that casts arguments to param types and invokes the method.
        /// All reflection/Expression stuff happens once at startup.
        /// </summary>
        private static Delegate BuildInvoker(
            object target,
            MethodInfo method,
            Type paramA,
            Type paramB,
            Type payloadType)
        {
            var targetConst = Expression.Constant(target);

            var payload = Expression.Parameter(typeof(object), "payload");
            var argA = Expression.Parameter(typeof(object), "a");
            var argB = Expression.Parameter(typeof(object), "b");

            var castPayload = Expression.Convert(payload, payloadType);
            var castA = Expression.Convert(argA, paramA);
            var castB = Expression.Convert(argB, paramB);

            var call = Expression.Call(targetConst, method, castPayload, castA, castB);

            var lambda = Expression.Lambda<Action<object?, object, object>>(call, payload, argA, argB);
            return lambda.Compile();
        }
    }
}
