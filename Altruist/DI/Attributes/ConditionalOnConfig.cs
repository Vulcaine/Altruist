// Altruist/ConditionalOnConfigAttribute.cs
namespace Altruist
{
    /// <summary>
    /// Conditionally registers a service based on configuration.
    /// Usage:
    /// [ConditionalOnConfig("A:B:C:D")]                // requires the key to exist
    [AttributeUsage(AttributeTargets.Class, AllowMultiple = true, Inherited = false)]
    public sealed class ConditionalOnConfigAttribute : Attribute
    {
        public string Path { get; }
        public string? HavingValue { get; }

        public string? KeyField { get; init; }

        /// <summary>
        /// When true (default), value comparison is case-insensitive.
        /// </summary>
        public bool CaseInsensitive { get; set; } = true;

        public ConditionalOnConfigAttribute(string path)
        {
            Path = path ?? throw new ArgumentNullException(nameof(path));
        }

        public ConditionalOnConfigAttribute(string path, string? havingValue = null, bool caseInsensitive = true)
        {
            Path = path ?? throw new ArgumentNullException(nameof(path));
            HavingValue = havingValue;
            CaseInsensitive = caseInsensitive;
        }
    }

    [AttributeUsage(AttributeTargets.Class, AllowMultiple = true)]
    public sealed class ConditionalOnAssemblyAttribute : Attribute
    {
        public string AssemblyName { get; }

        public ConditionalOnAssemblyAttribute(string assemblyName)
        {
            AssemblyName = assemblyName;
        }
    }
}
