using System.Linq.Expressions;
using System.Reflection;

using Microsoft.Extensions.Logging;

namespace Altruist.Physx
{
    public static class CollisionHandlerDiscovery
    {
        /// <summary>
        /// Discover all [CollisionHandler] classes in the given assemblies,
        /// create instances (via instanceFactory) and register all their
        /// [CollisionEvent] methods into CollisionHandlerRegistry.
        ///
        /// instanceFactory can be your DI container's resolver:
        ///    type => serviceProvider.GetService(type)
        /// falling back to Activator.CreateInstance if it returns null.
        /// </summary>
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

                // Require exactly two parameters: the two entities/components.
                if (pars.Length != 2)
                    throw new InvalidOperationException(
                        $"Method {type.Name}.{method.Name} marked with [CollisionEvent] must have exactly 2 parameters.");

                var paramA = pars[0].ParameterType;
                var paramB = pars[1].ParameterType;

                if (!paramA.IsClass || paramA.IsAbstract)
                    throw new InvalidOperationException(
                        $"First parameter of {type.Name}.{method.Name} must be a concrete reference type.");

                if (!paramB.IsClass || paramB.IsAbstract)
                    throw new InvalidOperationException(
                        $"Second parameter of {type.Name}.{method.Name} must be a concrete reference type.");

                if (method.ReturnType != typeof(void))
                    throw new InvalidOperationException(
                        $"Method {type.Name}.{method.Name} marked with [CollisionEvent] must return void.");

                // Build a compiled delegate Action<object, object> that:
                //   - casts the two objects to the method's parameter types
                //   - calls the method on the given instance.
                var invoker = BuildInvoker(instance, method, paramA, paramB);

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
        /// Builds an Action&lt;object, object&gt; that casts arguments to param types and invokes the method.
        /// All reflection/Expression stuff happens once at startup.
        /// </summary>
        private static Delegate BuildInvoker(
            object target,
            MethodInfo method,
            Type paramA,
            Type paramB)
        {
            var targetConst = Expression.Constant(target);

            var argA = Expression.Parameter(typeof(object), "a");
            var argB = Expression.Parameter(typeof(object), "b");

            var castA = Expression.Convert(argA, paramA);
            var castB = Expression.Convert(argB, paramB);

            var call = Expression.Call(targetConst, method, castA, castB);

            var lambda = Expression.Lambda<Action<object, object>>(call, argA, argB);
            return lambda.Compile();
        }
    }
}
