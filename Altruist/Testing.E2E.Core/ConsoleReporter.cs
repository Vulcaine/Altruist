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

    public ConsoleReporter() : this(Console.WriteLine) { }
    public ConsoleReporter(Action<string> writeLine) => _writeLine = writeLine;

    public void OnRunStarted(int totalTests)
    {
        _writeLine($"[Altruist E2E] Running {totalTests} test(s)...");
    }

    public void OnTestStarted(string fullName)
    {
        // Quiet — we report on finish so PASS/FAIL is on the same line.
    }

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
