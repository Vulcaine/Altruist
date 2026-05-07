/*
Copyright 2025 Aron Gere
Licensed under the Apache License, Version 2.0
*/

using System.Reflection;

using Xunit.Abstractions;
using Xunit.Sdk;

namespace Altruist.Testing.Internal;

/// <summary>
/// Custom test-collection factory that puts every <c>[AltruistIntegrationTest]</c>
/// class into ONE shared <c>"Altruist Live Server"</c> collection, while leaving
/// every other class on the default collection-per-class behavior.
///
/// <para><b>Why.</b> Integration tests share the process-singleton live server.
/// When two integration test classes run in parallel xUnit collections, their
/// TCP sessions interleave on the same listener — sync packets from one player
/// arrive on the other's socket, and tests that drain-and-assert against their
/// own VID become flaky. xUnit serializes tests within a collection, so
/// collapsing all integration classes into one collection is the cleanest fix.</para>
///
/// <para><c>[AltruistTest]</c> classes are unaffected: they use per-method DI
/// rebuild + per-class schema isolation, so parallel execution is safe (and
/// faster) for them.</para>
/// </summary>
public sealed class AltruistTestCollectionFactory : IXunitTestCollectionFactory
{
    private readonly ITestAssembly _testAssembly;
    private readonly CollectionPerClassTestCollectionFactory _default;
    private readonly Lazy<ITestCollection> _liveServerCollection;

    public AltruistTestCollectionFactory(ITestAssembly testAssembly, IMessageSink diagnosticMessageSink)
    {
        _testAssembly = testAssembly;
        _default = new CollectionPerClassTestCollectionFactory(testAssembly, diagnosticMessageSink);
        _liveServerCollection = new Lazy<ITestCollection>(
            () => new TestCollection(testAssembly, null, "Altruist Live Server"));
    }

    public string DisplayName => "Altruist (live-server collection + per-class default)";

    public ITestCollection Get(ITypeInfo testClass)
    {
        if (HasAltruistIntegrationTestAttribute(testClass))
            return _liveServerCollection.Value;
        return _default.Get(testClass);
    }

    private static bool HasAltruistIntegrationTestAttribute(ITypeInfo testClass)
    {
        if (testClass is IReflectionTypeInfo reflectionType)
            return reflectionType.Type.GetCustomAttribute<AltruistIntegrationTestAttribute>(inherit: true) is not null;

        // Non-reflection paths (rare in practice — assembly was loaded for inspection
        // only) fall through to per-class default. Test discovery happens on the
        // loaded test DLL, so reflection metadata is virtually always available.
        return false;
    }
}
