/*
Copyright 2025 Aron Gere
Licensed under the Apache License, Version 2.0
*/

using System.Reflection;

using Xunit.Abstractions;
using Xunit.Sdk;

namespace Altruist.Testing.Internal;

/// <summary>
/// Method runner that rebuilds the test class's constructor arguments before
/// every test case (every <c>[Fact]</c> AND every <c>[Theory]</c>+<c>[InlineData]</c>
/// row). Without this, all <c>[InlineData]</c> rows of a single <c>[Theory]</c>
/// share the same args — disposable test clients (<see cref="TestPlayerSession"/>,
/// <see cref="TestHttpClient"/>, <see cref="TestTcpClient"/>) get disposed by the
/// first row's <c>DisposeAsync</c> and the next row hits an
/// <c>ObjectDisposedException</c>.
///
/// <para>The base xUnit runner caches ctor args at method level, so we override
/// <see cref="RunTestCaseAsync"/> to call back into the framework's arg factory
/// once per case.</para>
/// </summary>
internal sealed class AltruistTestMethodRunner : XunitTestMethodRunner
{
    private readonly Func<object[]> _argFactory;
    private readonly IMessageSink _diagnosticMessageSink;

    public AltruistTestMethodRunner(
        Func<object[]> argFactory,
        ITestMethod testMethod,
        IReflectionTypeInfo @class,
        IReflectionMethodInfo method,
        IEnumerable<IXunitTestCase> testCases,
        IMessageSink diagnosticMessageSink,
        IMessageBus messageBus,
        ExceptionAggregator aggregator,
        CancellationTokenSource cancellationTokenSource,
        object[] constructorArguments)
        : base(testMethod, @class, method, testCases, diagnosticMessageSink, messageBus,
              aggregator, cancellationTokenSource, constructorArguments)
    {
        _argFactory = argFactory;
        _diagnosticMessageSink = diagnosticMessageSink;
    }

    protected override Task<RunSummary> RunTestCaseAsync(IXunitTestCase testCase)
    {
        var freshArgs = _argFactory();
        return testCase.RunAsync(
            _diagnosticMessageSink,
            MessageBus,
            freshArgs,
            new ExceptionAggregator(Aggregator),
            CancellationTokenSource);
    }
}
