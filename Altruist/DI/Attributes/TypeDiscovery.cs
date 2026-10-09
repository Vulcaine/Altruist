using System.Reflection;

namespace Altruist
{
    /// <summary>
    /// Reflection helpers for scanning assemblies, tolerant of assemblies that fail to load some types.
    /// Use these instead of raw <c>Assembly.GetTypes()</c> when implementing attribute-driven discovery.
    /// </summary>
    public static class TypeDiscovery
    {
        /// <summary>
        /// Finds non-abstract classes that carry <typeparamref name="TAttribute"/> directly (inherited attributes are ignored).
        /// </summary>
        /// <typeparam name="TAttribute">Attribute to look for.</typeparam>
        /// <param name="assemblies">Assemblies to scan, e.g. <c>AppDomain.CurrentDomain.GetAssemblies()</c>.</param>
        /// <returns>Matching types, lazily enumerated.</returns>
        public static IEnumerable<Type> FindTypesWithAttribute<TAttribute>(IEnumerable<Assembly> assemblies)
             where TAttribute : Attribute
        {
            return assemblies
                .SelectMany(SafeGetTypes)
                .Where(t => t.IsClass && !t.IsAbstract)
                .Where(t => t.GetCustomAttributes<TAttribute>(inherit: false).Any());
        }

        /// <summary>
        /// Returns <c>asm.GetTypes()</c>, or the types that did load when a <see cref="ReflectionTypeLoadException"/> occurs.
        /// </summary>
        /// <param name="asm">Assembly to read.</param>
        public static IEnumerable<Type> SafeGetTypes(Assembly asm)
        {
            try
            {
                return asm.GetTypes();
            }
            catch (ReflectionTypeLoadException ex)
            {
                return ex.Types.Where(t => t is not null)!;
            }
        }

        /// <summary>
        /// Finds all non-abstract classes that implement <typeparamref name="TInterface"/>.
        /// </summary>
        /// <typeparam name="TInterface">Interface type; anything else throws.</typeparam>
        /// <param name="assemblies">Assemblies to scan.</param>
        /// <exception cref="InvalidOperationException"><typeparamref name="TInterface"/> is not an interface.</exception>
        public static IEnumerable<Type> FindTypesImplementing<TInterface>(IEnumerable<Assembly> assemblies)
        {
            var interfaceType = typeof(TInterface);

            if (!interfaceType.IsInterface)
                throw new InvalidOperationException($"{interfaceType.FullName} is not an interface.");

            return assemblies
                .SelectMany(SafeGetTypes)
                .Where(t =>
                    t is not null &&
                    t.IsClass &&
                    !t.IsAbstract &&
                    interfaceType.IsAssignableFrom(t)
                );
        }

        /// <summary>
        /// Finds instance methods on a specific type that are decorated with TAttribute.
        /// Returns (MethodInfo, AttributeInstance) pairs.
        /// This is reusable for Gate discovery, Collision discovery, etc.
        /// </summary>
        /// <typeparam name="TAttribute">Method attribute to look for (not inherited from overridden methods).</typeparam>
        /// <param name="type">Type whose methods are scanned.</param>
        /// <param name="flags">Binding flags; defaults to public and non-public instance methods.</param>
        public static IEnumerable<(MethodInfo Method, TAttribute Attribute)>
            FindInstanceMethodsWithAttribute<TAttribute>(
                Type type,
                BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            where TAttribute : Attribute
        {
            foreach (var m in type.GetMethods(flags))
            {
                var attr = m.GetCustomAttribute<TAttribute>(inherit: false);
                if (attr != null)
                    yield return (m, attr);
            }
        }
    }
}
