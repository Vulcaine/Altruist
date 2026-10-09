/*
Copyright 2025 Aron Gere
Licensed under the Apache License, Version 2.0
*/

using Moq;

namespace Altruist.Testing;

/// <summary>
/// Replaces a constructor parameter's service with a <see cref="Mock{T}"/>.<see cref="Mock{T}.Object"/>
/// for one test method (each <c>[Fact]</c> gets a fresh mock in its freshly built container).
/// The mock replaces every registration of the parameter's type BEFORE the provider is built,
/// so other DI-resolved services in the same constructor see the mock as their dependency
/// (Spring <c>@MockBean</c> behavior). Only on constructors of <see cref="AltruistTestAttribute"/>
/// classes; get the <see cref="Mock{T}"/> back with <c>Mock.Get(parameter)</c> to set it up.
/// </summary>
[AttributeUsage(AttributeTargets.Parameter, Inherited = false, AllowMultiple = false)]
public sealed class MockAttribute : Attribute
{
    /// <summary>
    /// If true, creates the mock with <see cref="MockBehavior.Strict"/>. Defaults to <see cref="MockBehavior.Loose"/>.
    /// </summary>
    public bool Strict { get; set; }
}
