// Altruist/TypeDiscovery.cs
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
    }
}
