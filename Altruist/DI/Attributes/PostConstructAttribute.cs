/* 
Copyright 2025 Aron Gere
Licensed under the Apache License, Version 2.0
*/

namespace Altruist
{
    /// <summary>
    /// Marks a method to be invoked immediately after an instance is constructed
    /// and config-bound. Parameters are resolved via DI and/or [ConfigValue] just
    /// like constructor parameters.
    ///
    /// You can define multiple methods; they will be invoked in ascending Order.
    /// </summary>
    [AttributeUsage(AttributeTargets.Method, Inherited = true, AllowMultiple = false)]
    public sealed class PostConstructAttribute : Attribute
    {
        public int Order { get; }
        public PostConstructAttribute(int order = 0) => Order = order;
    }
}
