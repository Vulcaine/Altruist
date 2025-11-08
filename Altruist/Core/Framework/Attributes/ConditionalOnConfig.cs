// Altruist/ConditionalOnConfigAttribute.cs
namespace Altruist
{
    /// <summary>
    /// Conditionally registers a service based on configuration.
    /// Usage:
    /// [ConditionalOnConfig("A:B:C:D")]                // requires the key to exist
    /// [ConditionalOnConfig("A:B:C:D", "enabled")]     // requires the key's value to equal "enabled"
    /// [ConditionalOnConfig("feature:enabled", "true")]// common boolean gate
    /// Multiple attributes are AND-ed (all must match).
    /// </summary>
    [AttributeUsage(AttributeTargets.Class, AllowMultiple = true, Inherited = false)]
    public sealed class ConditionalOnConfigAttribute : Attribute
    {
        public string Path { get; }
        public string? HavingValue { get; }

        /// <summary>
        /// When true (default), value comparison is case-insensitive.
        /// </summary>
        public bool CaseInsensitive { get; set; } = true;

        public ConditionalOnConfigAttribute(string path)
        {
            Path = path ?? throw new ArgumentNullException(nameof(path));
        }

        public ConditionalOnConfigAttribute(string path, string havingValue)
        {
            Path = path ?? throw new ArgumentNullException(nameof(path));
            HavingValue = havingValue ?? throw new ArgumentNullException(nameof(havingValue));
        }
    }
}
