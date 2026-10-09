
namespace Altruist
{
    /// <summary>
    /// Marks a class as a database keyspace/schema descriptor so the database module discovers and registers it.
    /// The class must be a concrete, non-abstract implementation of <see cref="IKeyspace"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The Postgres module scans loaded assemblies for <c>[Keyspace]</c> classes implementing <see cref="IKeyspace"/>
    /// and registers each as a singleton (both as itself and as <see cref="IKeyspace"/>); it is constructed through DI,
    /// so constructor dependencies and <c>[AppConfigValue]</c> parameters are injected.
    /// </para>
    /// <para>
    /// Vaults are bound to a keyspace by matching <see cref="IKeyspace.Name"/> against the vault's
    /// <see cref="Altruist.UORM.VaultAttribute.Keyspace"/>, NOT against <see cref="Name"/> on this attribute (which is
    /// informational). Keep both the same. Declaring a keyspace class is optional: when none matches, a default
    /// schema with that name is used. Declare one only when the schema needs custom configuration.
    /// </para>
    /// </remarks>
    /// <example>
    /// <code>
    /// [Keyspace("analytics")]
    /// public sealed class AnalyticsSchema : IKeyspace
    /// {
    ///     public string Name =&gt; "analytics";
    ///     public IDatabaseServiceToken DatabaseToken =&gt; PostgresDBToken.Instance;
    /// }
    ///
    /// [Vault("events", Keyspace: "analytics")]
    /// public class EventVault : VaultModel { /* ... */ }
    /// </code>
    /// </example>
    [AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
    public sealed class KeyspaceAttribute : Attribute
    {
        /// <summary>Marks the class as a keyspace descriptor.</summary>
        /// <param name="name">Keyspace/schema name; should equal the class's <see cref="IKeyspace.Name"/>.</param>
        public KeyspaceAttribute(string name) => Name = name;
        /// <summary>Declared keyspace name (informational; vault binding uses <see cref="IKeyspace.Name"/>).</summary>
        public string Name { get; }
    }
}
