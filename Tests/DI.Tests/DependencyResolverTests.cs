using Altruist;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DI.Tests;

// ── Test services ──────────────────────────────────────────────────────

[Service] public class SimpleService { }

[Service] public class ServiceWithDep
{
    public SimpleService Simple { get; }
    public ServiceWithDep(SimpleService simple) => Simple = simple;
}

[Service] public class ServiceA
{
    public ServiceB B { get; }
    public ServiceA(ServiceB b) => B = b;
}

[Service] public class ServiceB
{
    public ServiceC C { get; }
    public ServiceB(ServiceC c) => C = c;
}

[Service] public class ServiceC { }

// Circular: X → Y → X
[Service] public class CircularX
{
    public CircularY Y { get; }
    public CircularX(CircularY y) => Y = y;
}

[Service] public class CircularY
{
    public CircularX X { get; }
    public CircularY(CircularX x) => X = x;
}

// Circular broken by Lazy: LazyA → Lazy<LazyB>, LazyB → LazyA
[Service] public class LazyA
{
    public Lazy<LazyB> B { get; }
    public LazyA(Lazy<LazyB> b) => B = b;
}

[Service] public class LazyB
{
    public LazyA A { get; }
    public LazyB(LazyA a) => A = a;
}

// Three-way cycle broken by Lazy: CycleP → CycleQ → Lazy<CycleR> → CycleP
[Service] public class CycleP
{
    public CycleQ Q { get; }
    public CycleP(CycleQ q) => Q = q;
}

[Service] public class CycleQ
{
    public Lazy<CycleR> R { get; }
    public CycleQ(Lazy<CycleR> r) => R = r;
}

[Service] public class CycleR
{
    public CycleP P { get; }
    public CycleR(CycleP p) => P = p;
}

// Deep chain: D1 → D2 → D3 → D4 → D5
[Service] public class D1 { public D2 D { get; } public D1(D2 d) => D = d; }
[Service] public class D2 { public D3 D { get; } public D2(D3 d) => D = d; }
[Service] public class D3 { public D4 D { get; } public D3(D4 d) => D = d; }
[Service] public class D4 { public D5 D { get; } public D4(D5 d) => D = d; }
[Service] public class D5 { }

// Multiple constructor parameters
[Service] public class MultiDep
{
    public SimpleService S1 { get; }
    public ServiceC S2 { get; }
    public D5 S3 { get; }
    public MultiDep(SimpleService s1, ServiceC s2, D5 s3) { S1 = s1; S2 = s2; S3 = s3; }
}

// Self-referencing (direct self-cycle)
[Service] public class SelfRef
{
    public SelfRef Self { get; }
    public SelfRef(SelfRef self) => Self = self;
}

// Diamond: DiamondTop → DiamondLeft + DiamondRight → DiamondBottom
[Service] public class DiamondBottom { }
[Service] public class DiamondLeft { public DiamondBottom B { get; } public DiamondLeft(DiamondBottom b) => B = b; }
[Service] public class DiamondRight { public DiamondBottom B { get; } public DiamondRight(DiamondBottom b) => B = b; }
[Service] public class DiamondTop
{
    public DiamondLeft L { get; }
    public DiamondRight R { get; }
    public DiamondTop(DiamondLeft l, DiamondRight r) { L = l; R = r; }
}

// ── Tests ──────────────────────────────────────────────────────────────

public class DependencyResolverTests
{
    private static (IServiceProvider provider, IConfiguration cfg, ILogger log) BuildProvider(params Type[] types)
    {
        var services = new ServiceCollection();
        var cfg = new ConfigurationBuilder().Build();
        var log = NullLogger.Instance;

        services.AddLogging(b => b.AddProvider(NullLoggerProvider.Instance));
        services.AddSingleton<IConfiguration>(cfg);

        DependencyResolver.EnsureConverters(services, cfg, log);

        foreach (var t in types)
        {
            DependencyPlanner.EnsureDependenciesRegistered(services, cfg, log, t);
            services.AddSingleton(t, sp => DependencyResolver.CreateWithConfiguration(sp, cfg, t, log));
        }

        return (services.BuildServiceProvider(), cfg, log);
    }

    [Fact]
    public void SimpleService_Resolves()
    {
        var (sp, _, _) = BuildProvider(typeof(SimpleService));
        var svc = sp.GetService<SimpleService>();
        Assert.NotNull(svc);
    }

    [Fact]
    public void SingleDependency_Resolves()
    {
        var (sp, _, _) = BuildProvider(typeof(ServiceWithDep));
        var svc = sp.GetRequiredService<ServiceWithDep>();
        Assert.NotNull(svc.Simple);
    }

    [Fact]
    public void TransitiveChain_Resolves()
    {
        var (sp, _, _) = BuildProvider(typeof(ServiceA));
        var a = sp.GetRequiredService<ServiceA>();
        Assert.NotNull(a.B);
        Assert.NotNull(a.B.C);
    }

    [Fact]
    public void DeepChain_Resolves()
    {
        var (sp, _, _) = BuildProvider(typeof(D1));
        var d1 = sp.GetRequiredService<D1>();
        Assert.NotNull(d1.D.D.D.D); // D1 → D2 → D3 → D4 → D5
    }

    [Fact]
    public void MultipleParameters_Resolves()
    {
        var (sp, _, _) = BuildProvider(typeof(MultiDep));
        var svc = sp.GetRequiredService<MultiDep>();
        Assert.NotNull(svc.S1);
        Assert.NotNull(svc.S2);
        Assert.NotNull(svc.S3);
    }

    [Fact]
    public void Diamond_SharedBottom_Resolves()
    {
        var (sp, _, _) = BuildProvider(typeof(DiamondTop));
        var top = sp.GetRequiredService<DiamondTop>();
        Assert.NotNull(top.L.B);
        Assert.NotNull(top.R.B);
        // Both sides share the same singleton bottom
        Assert.Same(top.L.B, top.R.B);
    }

    [Fact]
    public void Singleton_ReturnsSameInstance()
    {
        var (sp, _, _) = BuildProvider(typeof(SimpleService));
        var a = sp.GetRequiredService<SimpleService>();
        var b = sp.GetRequiredService<SimpleService>();
        Assert.Same(a, b);
    }

    // ── Circular dependency detection ──────────────────────────────────

    [Fact]
    public void DirectCircularDep_Throws()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            BuildProvider(typeof(CircularX)));
        Assert.Contains("Circular dependency detected", ex.Message);
    }

    [Fact]
    public void SelfReference_Throws()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            BuildProvider(typeof(SelfRef)));
        Assert.Contains("Circular dependency detected", ex.Message);
    }

    [Fact]
    public void CircularDep_ErrorContainsPath()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            BuildProvider(typeof(CircularX)));
        Assert.Contains("CircularX", ex.Message);
        Assert.Contains("CircularY", ex.Message);
    }

    // ── Lazy<T> support ────────────────────────────────────────────────

    [Fact]
    public void LazyDep_BreaksCircularDep()
    {
        // LazyA → Lazy<LazyB>, LazyB → LazyA — should NOT throw
        var (sp, _, _) = BuildProvider(typeof(LazyA), typeof(LazyB));
        var a = sp.GetRequiredService<LazyA>();
        Assert.NotNull(a);
        Assert.NotNull(a.B);
        // Lazy not yet resolved
        var b = a.B.Value;
        Assert.NotNull(b);
        Assert.Same(a, b.A); // circular reference works at runtime
    }

    [Fact]
    public void ThreeWayCycle_BrokenByLazy()
    {
        // CycleP → CycleQ → Lazy<CycleR> → CycleP
        var (sp, _, _) = BuildProvider(typeof(CycleP), typeof(CycleQ), typeof(CycleR));
        var p = sp.GetRequiredService<CycleP>();
        Assert.NotNull(p.Q);
        var r = p.Q.R.Value;
        Assert.NotNull(r);
        Assert.Same(p, r.P);
    }

    [Fact]
    public void LazyDep_ValueIsAccessible()
    {
        var (sp, _, _) = BuildProvider(typeof(LazyA), typeof(LazyB));
        var a = sp.GetRequiredService<LazyA>();
        // Lazy wraps the dependency
        Assert.NotNull(a.B);
        // Accessing .Value returns a valid instance
        var b = a.B.Value;
        Assert.NotNull(b);
        Assert.IsType<LazyB>(b);
    }
}
