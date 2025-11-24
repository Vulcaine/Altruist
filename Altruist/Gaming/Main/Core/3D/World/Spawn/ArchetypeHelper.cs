namespace Altruist.Gaming.ThreeD
{
    internal static class WorldObjectArchetypeHelper
    {
        public static string ResolveArchetype(Type type)
        {
            var attr = (WorldObjectAttribute?)Attribute.GetCustomAttribute(
                type,
                typeof(WorldObjectAttribute),
                inherit: false);

            if (attr == null || string.IsNullOrWhiteSpace(attr.Archetype))
            {
                throw new InvalidOperationException(
                    $"Type {type.FullName} must be annotated with [WorldObject(\"ArchetypeName\")] " +
                    "or override the Archetype property.");
            }

            return attr.Archetype;
        }
    }
}
