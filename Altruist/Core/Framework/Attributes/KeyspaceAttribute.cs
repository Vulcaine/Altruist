
namespace Altruist
{
    /// <summary>
    /// Marks a keyspace model type (must implement IScyllaKeyspace and have a public parameterless ctor).
    /// </summary>
    [AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
    public sealed class KeyspaceAttribute : Attribute
    {
        public KeyspaceAttribute(string name) => Name = name;
        public string Name { get; }
    }
}
