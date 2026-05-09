/*
Copyright 2025 Aron Gere
Licensed under the Apache License, Version 2.0
*/

using System.Diagnostics;
using System.Reflection;

using Xunit;

namespace Altruist.Testing.E2E;

/// <summary>
/// Reflection-based test runner for <see cref="AltruistE2ETestAttribute"/>
/// classes. Designed to run in any host that can load .NET reflection — server
/// CLI, console app, or Unity Editor (where xUnit's own runner doesn't
/// function). Server-side <c>dotnet test</c> still uses xUnit; this runner is
/// for the host where xUnit is unavailable.
///
/// <para><b>Discovery contract.</b> A test is any public instance method
/// marked <c>[Fact]</c> on a class marked
/// <see cref="AltruistE2ETestAttribute"/>. Methods may return <c>void</c> or
/// <see cref="Task"/>.</para>
///
/// <para><b>Construction.</b> Test classes are constructed via parameterless
/// constructor. Optional <see cref="IAsyncLifetime"/> hooks
/// (<c>InitializeAsync</c>/<c>DisposeAsync</c>) fire around each method.
/// Class-fixture (<c>IClassFixture&lt;T&gt;</c>) is <em>not</em> implemented in
/// this runner — server-side tests using a class fixture should run via
/// <c>dotnet test</c> (xUnit handles it there); Unity-side tests should keep
/// their setup logic in the test class itself or use <see cref="IAsyncLifetime"/>.</para>
///
/// <para><b>Concurrency.</b> Tests run sequentially. Parallel execution is a
/// future enhancement; sequential matches the shared-server model the E2E
/// stack uses today.</para>
/// </summary>
public sealed class AltruistTestRunner
{
    /// <summary>
    /// Discover and run every <see cref="AltruistE2ETestAttribute"/> class in
    /// the given assemblies. Returns the list of <see cref="TestResult"/> for
    /// every method invoked. Reporter callbacks fire as the run proceeds.
    /// </summary>
    public async Task<IReadOnlyList<TestResult>> RunAsync(
        IEnumerable<Assembly> assemblies,
        ITestReporter reporter,
        CancellationToken ct = default)
    {
        if (assemblies is null) throw new ArgumentNullException(nameof(assemblies));
        if (reporter is null) throw new ArgumentNullException(nameof(reporter));

        var plan = DiscoverTests(assemblies).ToList();
        reporter.OnRunStarted(plan.Count);

        var results = new List<TestResult>();
        foreach (var (testClass, method) in plan)
        {
            ct.ThrowIfCancellationRequested();
            var fullName = $"{testClass.FullName}.{method.Name}";
            reporter.OnTestStarted(fullName);

            var result = await RunOneAsync(testClass, method, fullName).ConfigureAwait(false);
            reporter.OnTestFinished(result);
            results.Add(result);
        }

        reporter.OnRunFinished(results);
        return results;
    }

    /// <summary>
    /// Synchronous wrapper for hosts that don't await (e.g. a Unity
    /// <c>[MenuItem]</c> that fires-and-forgets). Blocks until completion.
    /// </summary>
    public IReadOnlyList<TestResult> Run(IEnumerable<Assembly> assemblies, ITestReporter reporter)
        => RunAsync(assemblies, reporter).GetAwaiter().GetResult();

    private static IEnumerable<(Type Class, MethodInfo Method)> DiscoverTests(IEnumerable<Assembly> assemblies)
    {
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
                    yield return (t, m);
                }
            }
        }
    }

    private static async Task<TestResult> RunOneAsync(Type testClass, MethodInfo method, string fullName)
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
            // Unwrap reflection wrapping so the reporter sees the real exception.
            return new TestResult(fullName, TestOutcome.Failed, sw.Elapsed, tie.InnerException ?? tie);
        }
        catch (Exception ex)
        {
            sw.Stop();
            return new TestResult(fullName, TestOutcome.Failed, sw.Elapsed, ex);
        }
    }
}
