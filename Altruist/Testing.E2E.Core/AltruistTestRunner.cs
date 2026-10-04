/*
Copyright 2025 Aron Gere
Licensed under the Apache License, Version 2.0
*/

using System.Diagnostics;
using System.Reflection;

using Xunit;

namespace Altruist.Testing.E2E;

/// <summary>
/// One discovered test method. Yielded by <see cref="AltruistTestRunner.Discover"/>
/// and accepted by <see cref="AltruistTestRunner.RunAsync(System.Collections.Generic.IEnumerable{TestCase}, ITestReporter, System.Threading.CancellationToken)"/>
/// or <see cref="AltruistTestRunner.RunOneAsync"/>. Hosts (e.g. a Unity Editor
/// panel) can show the discovered list to the user and drive runs
/// individually.
/// </summary>
public sealed class TestCase
{
    public Type Class { get; }
    public MethodInfo Method { get; }
    public string FullName => $"{Class.FullName}.{Method.Name}";

    public TestCase(Type cls, MethodInfo method)
    {
        Class = cls ?? throw new ArgumentNullException(nameof(cls));
        Method = method ?? throw new ArgumentNullException(nameof(method));
    }

    public override string ToString() => FullName;
}

/// <summary>
/// Reflection-based test runner for <see cref="AltruistE2ETestAttribute"/>
/// classes. Designed to run in any host that can load .NET reflection — server
/// CLI, console app, or Unity Editor (where xUnit's own runner doesn't
/// function). Server-side <c>dotnet test</c> still uses xUnit; this runner is
/// for hosts where xUnit is unavailable.
///
/// <para><b>Discovery contract.</b> A test is any public instance method
/// marked <c>[Fact]</c> on a class marked
/// <see cref="AltruistE2ETestAttribute"/>. Methods may return <c>void</c> or
/// <see cref="Task"/>.</para>
///
/// <para><b>Construction.</b> Test classes are constructed via parameterless
/// constructor. Optional <see cref="IAsyncLifetime"/> hooks
/// (<c>InitializeAsync</c>/<c>DisposeAsync</c>) fire around each method.
/// <c>IClassFixture&lt;T&gt;</c> is <em>not</em> implemented in this runner
/// — server-side tests using one should run via <c>dotnet test</c>; Unity
/// hosts should keep setup logic in the test class itself or use
/// <see cref="IAsyncLifetime"/>.</para>
///
/// <para><b>Concurrency.</b> Tests run sequentially. Parallel execution is a
/// future enhancement; sequential matches the shared-server model the E2E
/// stack uses today.</para>
/// </summary>
public sealed class AltruistTestRunner
{
    /// <summary>
    /// Reflect over <paramref name="assemblies"/> and return every test the
    /// runner would invoke. Pure read — no side effects, safe to call on the
    /// editor UI thread.
    /// </summary>
    public static IReadOnlyList<TestCase> Discover(IEnumerable<Assembly> assemblies)
    {
        if (assemblies is null) throw new ArgumentNullException(nameof(assemblies));
        var cases = new List<TestCase>();

        foreach (var asm in assemblies)
        {
            if (asm is null || asm.IsDynamic) continue;

            Type[] types;
            try { types = asm.GetTypes(); }
            catch (ReflectionTypeLoadException ex) { types = ex.Types.Where(t => t is not null).ToArray()!; }

            foreach (var t in types)
            {
                if (t is null || !t.IsClass || t.IsAbstract) continue;
                if (t.GetCustomAttribute<AltruistE2ETestAttribute>(inherit: true) is null) continue;

                foreach (var m in t.GetMethods(BindingFlags.Public | BindingFlags.Instance))
                {
                    if (m.GetCustomAttribute<FactAttribute>(inherit: true) is null) continue;
                    if (m.GetParameters().Length != 0) continue; // skip [Theory]/[InlineData] for v0
                    cases.Add(new TestCase(t, m));
                }
            }
        }

        return cases;
    }

    /// <summary>
    /// Discover and run every <see cref="AltruistE2ETestAttribute"/> class in
    /// the given assemblies. Convenience overload — equivalent to
    /// <c>RunAsync(Discover(assemblies), reporter, ct)</c>.
    /// </summary>
    public Task<IReadOnlyList<TestResult>> RunAsync(
        IEnumerable<Assembly> assemblies,
        ITestReporter reporter,
        CancellationToken ct = default)
        => RunAsync(Discover(assemblies), reporter, ct);

    /// <summary>
    /// Run a specific subset of tests. Reporter callbacks fire as the run
    /// proceeds; observe <paramref name="reporter"/> for streaming progress
    /// in a UI.
    /// </summary>
    public async Task<IReadOnlyList<TestResult>> RunAsync(
        IEnumerable<TestCase> tests,
        ITestReporter reporter,
        CancellationToken ct = default)
    {
        if (tests is null) throw new ArgumentNullException(nameof(tests));
        if (reporter is null) throw new ArgumentNullException(nameof(reporter));

        var plan = tests as IList<TestCase> ?? tests.ToList();
        reporter.OnRunStarted(plan.Count);

        var results = new List<TestResult>();
        foreach (var tc in plan)
        {
            ct.ThrowIfCancellationRequested();
            reporter.OnTestStarted(tc.FullName);
            var result = await RunOneAsync(tc, ct).ConfigureAwait(false);
            reporter.OnTestFinished(result);
            results.Add(result);
        }

        reporter.OnRunFinished(results);
        return results;
    }

    /// <summary>
    /// Run a single test case. Callers driving a UI panel call this directly
    /// (no reporter required) and update their own row state from the
    /// returned <see cref="TestResult"/>.
    /// </summary>
    public Task<TestResult> RunOneAsync(TestCase testCase, CancellationToken ct = default)
        => RunOneInternalAsync(testCase.Class, testCase.Method, testCase.FullName);

    /// <summary>
    /// Synchronous wrapper for hosts that don't await (e.g. a Unity
    /// <c>[MenuItem]</c> that fires-and-forgets and doesn't drive
    /// MonoBehaviours). Blocks until completion — do <em>not</em> use this
    /// from a play-mode host where tests await Unity main-loop ticks; it
    /// will deadlock.
    /// </summary>
    public IReadOnlyList<TestResult> Run(IEnumerable<Assembly> assemblies, ITestReporter reporter)
        => RunAsync(assemblies, reporter).GetAwaiter().GetResult();

    private static async Task<TestResult> RunOneInternalAsync(Type testClass, MethodInfo method, string fullName)
    {
        var sw = Stopwatch.StartNew();
        object? instance = null;
        try
        {
            instance = Activator.CreateInstance(testClass)
                ?? throw new InvalidOperationException(
                    $"Could not construct {testClass.FullName} — verify it has a public parameterless constructor.");

            if (instance is IAsyncLifetime lifecycle)
                await lifecycle.InitializeAsync().ConfigureAwait(false);

            try
            {
                var ret = method.Invoke(instance, Array.Empty<object>());
                if (ret is Task task) await task.ConfigureAwait(false);
            }
            finally
            {
                if (instance is IAsyncLifetime lifecycle2)
                {
                    try { await lifecycle2.DisposeAsync().ConfigureAwait(false); }
                    catch { /* dispose errors don't fail the test, only invoke errors do */ }
                }
                else if (instance is IDisposable disposable)
                {
                    try { disposable.Dispose(); } catch { /* same */ }
                }
            }

            sw.Stop();
            return new TestResult(fullName, TestOutcome.Passed, sw.Elapsed);
        }
        catch (TargetInvocationException tie)
        {
            sw.Stop();
            return new TestResult(fullName, TestOutcome.Failed, sw.Elapsed, tie.InnerException ?? tie);
        }
        catch (Exception ex)
        {
            sw.Stop();
            return new TestResult(fullName, TestOutcome.Failed, sw.Elapsed, ex);
        }
    }
}
