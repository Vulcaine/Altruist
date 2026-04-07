namespace Altruist
{
    [AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
    public sealed class ConfigurationPropertiesAttribute : Attribute
    {
        public string Path { get; }
        public ConfigurationPropertiesAttribute(string path) => Path = path ?? throw new ArgumentNullException(nameof(path));
    }
}
