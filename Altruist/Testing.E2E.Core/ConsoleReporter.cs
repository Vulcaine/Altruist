/*
Copyright 2025 Aron Gere
Licensed under the Apache License, Version 2.0
*/

namespace Altruist.Testing.E2E;

/// <summary>
/// Reporter that prints to stdout (or a caller-supplied <see cref="Action{T}"/>
/// sink, which Unity uses to redirect to <c>Debug.Log</c>). Output mimics the
/// shape of <c>dotnet test</c>: per-test PASS/FAIL line, then a summary.
/// </summary>
public sealed class ConsoleReporter : ITestReporter
{
    private readonly Action<string> _writeLine;

    /// <summary>A reporter that writes to <see cref="Console"/>.</summary>
    public ConsoleReporter() : this(Console.WriteLine) { }
    /// <summary>A reporter that writes each line to <paramref name="writeLine"/> (a game-engine log, a UI panel).</summary>
    /// <param name="writeLine">The line sink.</param>
    public ConsoleReporter(Action<string> writeLine) => _writeLine = writeLine;

    /// <inheritdoc/>
    public void OnRunStarted(int totalTests)
    {
        _writeLine($"[Altruist E2E] Running {totalTests} test(s)...");
    }

    /// <summary>Prints nothing (the result line is written when the test finishes).</summary>
    /// <param name="fullName">The test's full name.</param>
    public void OnTestStarted(string fullName)
    {
        // Quiet — we report on finish so PASS/FAIL is on the same line.
    }

    /// <summary>Prints one PASS/FAIL/SKIP line with the duration, plus the exception and its first stack frames on failure.</summary>
    /// <param name="result">The result.</param>
    public void OnTestFinished(TestResult result)
    {
        var symbol = result.Outcome switch
        {
            TestOutcome.Passed => "PASS",
            TestOutcome.Failed => "FAIL",
            TestOutcome.Skipped => "SKIP",
            _ => "????",
        };
        _writeLine($"  [{symbol}] {result.FullName} ({result.Duration.TotalMilliseconds:F0} ms)");
        if (result.Outcome == TestOutcome.Failed && result.Failure is not null)
        {
            _writeLine($"        {result.Failure.GetType().Name}: {result.Failure.Message}");
            // First few stack frames — full trace is overkill for a console summary.
            var stack = result.Failure.StackTrace ?? "";
            foreach (var line in stack.Split('\n').Take(4))
                _writeLine($"        {line.TrimEnd()}");
        }
    }

    /// <summary>Prints the passed / failed / skipped counts and the total duration.</summary>
    /// <param name="results">Every result of the run.</param>
    public void OnRunFinished(IReadOnlyList<TestResult> results)
    {
        int passed = 0, failed = 0, skipped = 0;
        TimeSpan total = TimeSpan.Zero;
        foreach (var r in results)
        {
            switch (r.Outcome)
            {
                case TestOutcome.Passed: passed++; break;
                case TestOutcome.Failed: failed++; break;
                case TestOutcome.Skipped: skipped++; break;
            }
            total += r.Duration;
        }
        _writeLine($"[Altruist E2E] {passed} passed, {failed} failed, {skipped} skipped — {total.TotalSeconds:F2}s total.");
    }
}
