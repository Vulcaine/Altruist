namespace Altruist
{
    /// <summary>
    /// Binds a plain configuration class (POCO) to the configuration section at <see cref="Path"/> once at startup and
    /// registers the bound instance as a singleton, so services can inject it as a typed settings object.
    /// </summary>
    /// <remarks>
    /// <para>Processed by <see cref="AltruistDI.BindConfigurationClasses"/> before services are registered. The class needs
    /// a public parameterless constructor; properties are bound with <c>ConfigurationBinder.Bind</c> (case-insensitive
    /// names). If the section does not exist the class is NOT registered, so injecting it will fail; give such
    /// consumers a fallback or ensure the section exists.</para>
    /// <para>If the section is a list (children keyed 0, 1, ...), a <c>List&lt;T&gt;</c> is bound and registered as
    /// <c>List&lt;T&gt;</c>, <c>IEnumerable&lt;T&gt;</c> and <c>IReadOnlyList&lt;T&gt;</c> (not as <c>T</c>).</para>
    /// <para>The values are a snapshot: later config reloads are not reflected. For values that must follow
    /// reloads use <see cref="ILiveConfigValue{T}"/> via <see cref="AppConfigValueAttribute"/>; for a single value
    /// use <see cref="AppConfigValueAttribute"/> on a constructor parameter.</para>
    /// </remarks>
    /// <example>
    /// <code>
    /// [ConfigurationProperties("myapp:mail")]
    /// public sealed class MailSettings
    /// {
    ///     public string Host { get; set; } = "localhost";
    ///     public int Port { get; set; } = 25;
    /// }
    ///
    /// [Service]
    /// public sealed class Mailer { public Mailer(MailSettings settings) { ... } }
    /// </code>
    /// </example>
    [AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
    public sealed class ConfigurationPropertiesAttribute : Attribute
    {
        /// <summary>Colon-separated configuration section path, e.g. <c>altruist:server</c>.</summary>
        public string Path { get; }

        /// <summary>Binds the class to the section at <paramref name="path"/>.</summary>
        /// <param name="path">Configuration section path.</param>
        public ConfigurationPropertiesAttribute(string path) => Path = path ?? throw new ArgumentNullException(nameof(path));
    }
}
