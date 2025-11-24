using System.Reflection;

namespace Altruist
{
    public static class TypeDiscovery
    {
        public static IEnumerable<Type> FindTypesWithAttribute<TAttribute>(IEnumerable<Assembly> assemblies)
             where TAttribute : Attribute
        {
            return assemblies
                .SelectMany(SafeGetTypes)
                .Where(t => t.IsClass && !t.IsAbstract)
                .Where(t => t.GetCustomAttributes<TAttribute>(inherit: false).Any());
        }

        public static IEnumerable<Type> SafeGetTypes(Assembly asm)
        {
            try
            { return asm.GetTypes(); }
            catch (ReflectionTypeLoadException ex) { return ex.Types.Where(t => t is not null)!; }
        }

        /// <summary>
        /// Finds instance methods on a specific type that are decorated with TAttribute.
        /// Returns (MethodInfo, AttributeInstance) pairs.
        /// This is reusable for Gate discovery, Collision discovery, etc.
        /// </summary>
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
