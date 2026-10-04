namespace Altruist
{
    /// <summary>
    /// Registers the annotated class only when no other class registers <see cref="ServiceType"/>:
    /// a framework default that an application replaces by declaring its own
    /// <c>[Service(typeof(ServiceType))]</c>. Other classes that are themselves conditional on the
    /// same missing service, or whose <c>[ConditionalOnConfig]</c> does not match, do not count.
    /// </summary>
    [AttributeUsage(AttributeTargets.Class, AllowMultiple = true, Inherited = false)]
    public sealed class ConditionalOnMissingServiceAttribute : Attribute
    {
        public Type ServiceType { get; }

        public ConditionalOnMissingServiceAttribute(Type serviceType)
        {
            ServiceType = serviceType ?? throw new ArgumentNullException(nameof(serviceType));
        }
    }
}
