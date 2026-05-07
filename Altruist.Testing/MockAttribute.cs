/*
Copyright 2025 Aron Gere
Licensed under the Apache License, Version 2.0
*/

using Moq;

namespace Altruist.Testing;

/// <summary>
/// Replaces a constructor parameter's service with a <see cref="Mock{T}"/>.<see cref="Mock{T}.Object"/>
/// for the lifetime of this test class. The mock is registered into the per-class
/// child container BEFORE building the provider, so other DI-resolved services in
/// the same constructor see the mock as their dependency (Spring <c>@MockBean</c> behavior).
/// </summary>
[AttributeUsage(AttributeTargets.Parameter, Inherited = false, AllowMultiple = false)]
public sealed class MockAttribute : Attribute
{
    /// <summary>
    /// If true, creates the mock with <see cref="MockBehavior.Strict"/>. Defaults to <see cref="MockBehavior.Loose"/>.
    /// </summary>
    public bool Strict { get; set; }
}
