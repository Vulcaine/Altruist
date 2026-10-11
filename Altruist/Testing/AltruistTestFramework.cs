/*
Copyright 2025 Aron Gere
Licensed under the Apache License, Version 2.0
*/

using System.Reflection;

using Altruist.Testing.Internal;

using Xunit.Abstractions;
using Xunit.Sdk;

namespace Altruist.Testing;

/// <summary>
/// Custom xUnit v2 test framework. Classes marked <see cref="AltruistTestAttribute"/>
/// are constructed via the Altruist DI container; all other classes use default xUnit behavior.
///
/// Wire it once per test assembly:
/// <code>
/// [assembly: Xunit.TestFramework("Altruist.Testing.AltruistTestFramework", "Altruist.Testing")]
/// </code>
///
/// <para>Then pick per class: <see cref="AltruistTestAttribute"/> (DI-resolved, per-class database
/// schema, <see cref="MockAttribute"/> mocks), <see cref="AltruistIntegrationTestAttribute"/> (a live
/// server booted in-process, test clients injected), or <c>[AltruistE2ETest]</c> with
/// <c>AltruistE2EFixture</c> (client stack against an externally running server).</para>
/// </summary>
public sealed class AltruistTestFramework : XunitTestFramework
{
    /// <summary>Created by xUnit from the assembly attribute.</summary>
    /// <param name="messageSink">xUnit's diagnostic sink.</param>
    public AltruistTestFramework(IMessageSink messageSink) : base(messageSink)
    {
    }

    /// <summary>Returns the Altruist executor, whose runners build DI-managed test classes.</summary>
    /// <param name="assemblyName">The test assembly.</param>
    /// <returns>The executor.</returns>
    protected override ITestFrameworkExecutor CreateExecutor(AssemblyName assemblyName)
        => new AltruistTestExecutor(assemblyName, SourceInformationProvider, DiagnosticMessageSink);
}
