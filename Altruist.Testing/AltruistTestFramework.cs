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
/// </summary>
public sealed class AltruistTestFramework : XunitTestFramework
{
    public AltruistTestFramework(IMessageSink messageSink) : base(messageSink)
    {
    }

    protected override ITestFrameworkExecutor CreateExecutor(AssemblyName assemblyName)
        => new AltruistTestExecutor(assemblyName, SourceInformationProvider, DiagnosticMessageSink);
}
