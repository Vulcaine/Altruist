/*
Copyright 2025 Aron Gere
Licensed under the Apache License, Version 2.0
*/

namespace Altruist.Testing.E2E;

/// <summary>Outcome of a single <c>[Fact]</c> method on an
/// <c>[AltruistE2ETest]</c> class.</summary>
public enum TestOutcome { Passed, Failed, Skipped }

/// <summary>
/// Result record for one test method invocation. Reporters consume these and
/// either print them, write JUnit XML, or surface them in a UI panel.
/// </summary>
public sealed class TestResult
{
    /// <summary>Fully qualified name: <c>Namespace.ClassName.MethodName</c>.</summary>
    public string FullName { get; }
    public TestOutcome Outcome { get; }
    public TimeSpan Duration { get; }
    /// <summary>Non-null on <see cref="TestOutcome.Failed"/>.</summary>
    public Exception? Failure { get; }

    public TestResult(string fullName, TestOutcome outcome, TimeSpan duration, Exception? failure = null)
    {
        FullName = fullName;
        Outcome = outcome;
        Duration = duration;
        Failure = failure;
    }
}

/// <summary>Output sink for test results. Implementations: console, JUnit XML,
/// Unity log, custom UI window. The runner calls <see cref="OnTestStarted"/>
/// before each test and <see cref="OnTestFinished"/> after.</summary>
public interface ITestReporter
{
    void OnRunStarted(int totalTests);
    void OnTestStarted(string fullName);
    void OnTestFinished(TestResult result);
    void OnRunFinished(IReadOnlyList<TestResult> results);
}
