// Altruist/ConditionalOnConfigAttribute.cs
namespace Altruist
{
    /// <summary>
    /// Registers the annotated <see cref="ServiceAttribute"/> / <see cref="ServiceConfigurationAttribute"/> class only when
    /// configuration matches. Two modes:
    /// <list type="bullet">
    /// <item><description><b>Gate</b> (no <see cref="KeyField"/>): the class is registered only if the section at
    /// <see cref="Path"/> exists, or, when <see cref="HavingValue"/> is given, its value equals it (trimmed;
    /// case-insensitive unless <see cref="CaseInsensitive"/> is false). Multiple gate attributes must all match.</description></item>
    /// <item><description><b>List</b> (<see cref="KeyField"/> set): <see cref="Path"/> points at a config list; one
    /// keyed instance is registered per item, keyed by the item's <see cref="KeyField"/> value, and constructed against that
    /// item's section (so <see cref="AppConfigValueAttribute"/> paths starting with <c>*</c>, e.g. <c>"*:gravity"</c>,
    /// read from the item). Every instance is also registered unkeyed, so <c>IEnumerable&lt;T&gt;</c> yields all of them.
    /// Only one list-style attribute per class is allowed; an item without the key field fails startup.</description></item>
    /// </list>
    /// Also used by <see cref="DependencyPlanner"/> when choosing implementations and by the missing-dependency diagnostics,
    /// which list the keys to add. Gate mode is additionally honoured for MVC controllers and portals (a gated-off
    /// controller or portal gets no route).
    /// </summary>
    /// <remarks>
    /// To provide a default only when no other implementation exists, use <see cref="ConditionalOnMissingServiceAttribute"/> instead.
    /// </remarks>
    /// <example>
    /// <code>
    /// [Service(typeof(ICache))]
    /// [ConditionalOnConfig("myapp:cache:provider", havingValue: "redis")]
    /// public sealed class RedisCache : ICache { ... }
    ///
    /// // altruist:game:worlds:items: [ { id: "w1", gravity: {...} }, { id: "w2", ... } ]
    /// [Service(typeof(IWorld))]
    /// [ConditionalOnConfig("altruist:game:worlds:items", KeyField = "id")]
    /// public sealed class World : IWorld
    /// {
    ///     public World([AppConfigValue("*:id")] string id) { ... }   // resolved per item
    /// }
    /// // inject one: [ServiceKey("w1")] IWorld world;  all: IEnumerable&lt;IWorld&gt; worlds
    /// </code>
    /// </example>
    [AttributeUsage(AttributeTargets.Class, AllowMultiple = true, Inherited = false)]
    public sealed class ConditionalOnConfigAttribute : Attribute
    {
        /// <summary>Colon-separated configuration path to test (gate mode) or the list section (list mode).</summary>
        public string Path { get; }

        /// <summary>Required value at <see cref="Path"/>; null or empty means "the section only has to exist".</summary>
        public string? HavingValue { get; }

        /// <summary>
        /// Switches to list mode: the field of each list item whose value becomes that instance's service key
        /// (e.g. <c>"id"</c>). Null for a plain gate.
        /// </summary>
        public string? KeyField { get; init; }

        /// <summary>
        /// When true (default), value comparison is case-insensitive.
        /// </summary>
        public bool CaseInsensitive { get; set; } = true;

        /// <summary>Requires <paramref name="path"/> to exist (or, with <see cref="KeyField"/>, to be a list).</summary>
        /// <param name="path">Configuration path, e.g. <c>altruist:server:transport:tcp:enabled</c>.</param>
        public ConditionalOnConfigAttribute(string path)
        {
            Path = path ?? throw new ArgumentNullException(nameof(path));
        }

        /// <summary>Requires the value at <paramref name="path"/> to equal <paramref name="havingValue"/>.</summary>
        /// <param name="path">Configuration path.</param>
        /// <param name="havingValue">Expected value; null means the key only has to exist.</param>
        /// <param name="caseInsensitive">Whether the comparison ignores case (default true).</param>
        public ConditionalOnConfigAttribute(string path, string? havingValue = null, bool caseInsensitive = true)
        {
            Path = path ?? throw new ArgumentNullException(nameof(path));
            HavingValue = havingValue;
            CaseInsensitive = caseInsensitive;
        }
    }

    /// <summary>
    /// Declares that the annotated class should only be active when the named assembly is present.
    /// </summary>
    /// <remarks>
    /// Note: the DI registration pipeline (<see cref="DependencyResolver.ShouldRegister"/>) does not currently evaluate
    /// this attribute; it is informational unless a specific discovery path checks it. Use
    /// <see cref="ConditionalOnConfigAttribute"/> for gating that is enforced.
    /// </remarks>
    [AttributeUsage(AttributeTargets.Class, AllowMultiple = true)]
    public sealed class ConditionalOnAssemblyAttribute : Attribute
    {
        /// <summary>Simple name of the required assembly (e.g. <c>Altruist.Dashboard</c>).</summary>
        public string AssemblyName { get; }

        /// <summary>Declares the required assembly.</summary>
        /// <param name="assemblyName">Simple assembly name.</param>
        public ConditionalOnAssemblyAttribute(string assemblyName)
        {
            AssemblyName = assemblyName;
        }
    }
}
