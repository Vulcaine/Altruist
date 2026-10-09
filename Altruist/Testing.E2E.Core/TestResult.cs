/*
Copyright 2025 Aron Gere
Licensed under the Apache License, Version 2.0
*/

namespace Altruist.Testing.E2E;

/// <summary>Outcome of a single <c>[Fact]</c> method on an
/// <c>[AltruistE2ETest]</c> class.</summary>
public enum TestOutcome
{
    /// <summary>The test ran and passed.</summary>
    Passed,
    /// <summary>The test ran and failed (assertion or exception).</summary>
    Failed,
    /// <summary>The test was skipped.</summary>
    Skipped,
}

/// <summary>
/// Result record for one test method invocation. Reporters consume these and
/// either print them, write JUnit XML, or surface them in a UI panel.
/// </summary>
public sealed class TestResult
{
    /// <summary>Fully qualified name: <c>Namespace.ClassName.MethodName</c>.</summary>
    public string FullName { get; }
    /// <summary>Passed, failed or skipped.</summary>
    public TestOutcome Outcome { get; }
    /// <summary>Wall time of the test, including its lifetime hooks.</summary>
    public TimeSpan Duration { get; }
    /// <summary>Non-null on <see cref="TestOutcome.Failed"/>.</summary>
    public Exception? Failure { get; }

    /// <summary>A result.</summary>
    /// <param name="fullName">The test's full name.</param>
    /// <param name="outcome">The outcome.</param>
    /// <param name="duration">How long it ran.</param>
    /// <param name="failure">The exception of a failed test, or null.</param>
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
    /// <summary>Before the first test, with the number of tests planned.</summary>
    void OnRunStarted(int totalTests);
    /// <summary>Before each test.</summary>
    void OnTestStarted(string fullName);
    /// <summary>After each test, with its result.</summary>
    void OnTestFinished(TestResult result);
    /// <summary>After the last test, with every result.</summary>
    void OnRunFinished(IReadOnlyList<TestResult> results);
}
