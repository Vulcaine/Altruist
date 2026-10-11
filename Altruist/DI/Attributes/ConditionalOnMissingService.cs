namespace Altruist
{
    /// <summary>
    /// Registers the annotated class only when no other class registers <see cref="ServiceType"/>:
    /// a framework default that an application replaces by declaring its own
    /// <c>[Service(typeof(ServiceType))]</c>. Other classes that are themselves conditional on the
    /// same missing service, or whose <c>[ConditionalOnConfig]</c> does not match, do not count.
    /// A registration already present in the service collection when this class is processed (e.g. one added
    /// by hand or by a <see cref="BeanAttribute"/> method) also suppresses it.
    /// </summary>
    /// <remarks>
    /// Use this for "default implementation" services; use <see cref="ConditionalOnConfigAttribute"/> when the choice
    /// should be driven by a config key instead. May be applied several times (all named services must be missing).
    /// </remarks>
    /// <example>
    /// <code>
    /// [Service(typeof(IPasswordHasher))]
    /// [ConditionalOnMissingService(typeof(IPasswordHasher))]
    /// public sealed class DefaultPasswordHasher : IPasswordHasher { ... }
    ///
    /// // In the application: this wins and the default is not registered.
    /// [Service(typeof(IPasswordHasher))]
    /// public sealed class Argon2Hasher : IPasswordHasher { ... }
    /// </code>
    /// </example>
    [AttributeUsage(AttributeTargets.Class, AllowMultiple = true, Inherited = false)]
    public sealed class ConditionalOnMissingServiceAttribute : Attribute
    {
        /// <summary>The service type that must not be registered by any other class.</summary>
        public Type ServiceType { get; }

        /// <summary>Registers the annotated class only if <paramref name="serviceType"/> is otherwise unregistered.</summary>
        /// <param name="serviceType">Service type to check.</param>
        public ConditionalOnMissingServiceAttribute(Type serviceType)
        {
            ServiceType = serviceType ?? throw new ArgumentNullException(nameof(serviceType));
        }
    }
}
