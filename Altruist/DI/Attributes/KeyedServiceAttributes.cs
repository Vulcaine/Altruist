// KeyedServiceAttribute.cs
/*
Copyright 2025 Aron Gere

Licensed under the Apache License, Version 2.0
*/

namespace Altruist
{
    /// <summary>
    /// Resolves a constructor, <see cref="BeanAttribute"/>-method or <see cref="PostConstructAttribute"/>-method parameter
    /// from a keyed service registration (keys come from <see cref="BeanAttribute.Name"/> or the item key of a list-style
    /// <see cref="ConditionalOnConfigAttribute"/>).
    /// </summary>
    /// <remarks>
    /// <para>Only parameters are honoured by <see cref="DependencyResolver"/>; the attribute is allowed on fields and
    /// properties but those are not injected. If no service is registered under the key, startup / resolution fails.</para>
    /// <para>Without this attribute a parameter gets the unkeyed registration; to receive every instance inject
    /// <c>IEnumerable&lt;T&gt;</c> instead.</para>
    /// </remarks>
    /// <example>
    /// <code>
    /// public MySystem([ServiceKey("world-1")] IWorld world) { ... }
    /// </code>
    /// </example>
    [AttributeUsage(AttributeTargets.Parameter | AttributeTargets.Property | AttributeTargets.Field,
        AllowMultiple = false, Inherited = true)]
    public sealed class ServiceKeyAttribute : Attribute
    {
        /// <summary>The service key to resolve.</summary>
        public string Key { get; }

        /// <summary>Resolves the parameter from the registration keyed <paramref name="key"/>.</summary>
        /// <param name="key">Service key; must not be null or whitespace.</param>
        /// <exception cref="ArgumentException"><paramref name="key"/> is null or whitespace.</exception>
        public ServiceKeyAttribute(string key)
        {
            if (string.IsNullOrWhiteSpace(key))
                throw new ArgumentException("Keyed service key cannot be null or whitespace.", nameof(key));

            Key = key;
        }
    }
}
