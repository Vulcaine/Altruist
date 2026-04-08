// KeyedServiceAttribute.cs
/*
Copyright 2025 Aron Gere

Licensed under the Apache License, Version 2.0
*/

namespace Altruist
{
    /// <summary>
    /// Marks a constructor parameter / field / property to be resolved
    /// from a keyed service registration.
    ///
    /// Example:
    ///   public MySystem([ServiceKey("world-1")] IWorld world)
    /// </summary>
    [AttributeUsage(AttributeTargets.Parameter | AttributeTargets.Property | AttributeTargets.Field,
        AllowMultiple = false, Inherited = true)]
    public sealed class ServiceKeyAttribute : Attribute
    {
        public string Key { get; }

        public ServiceKeyAttribute(string key)
        {
            if (string.IsNullOrWhiteSpace(key))
                throw new ArgumentException("Keyed service key cannot be null or whitespace.", nameof(key));

            Key = key;
        }
    }
}
