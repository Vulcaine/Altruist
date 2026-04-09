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

// Three-way direct cycle (no Lazy): TriA → TriB → TriC → TriA
[Service] public class TriA { public TriB B { get; } public TriA(TriB b) => B = b; }
[Service] public class TriB { public TriC C { get; } public TriB(TriC c) => C = c; }
[Service] public class TriC { public TriA A { get; } public TriC(TriA a) => A = a; }

// Self-referencing via Lazy (should work)
[Service] public class LazySelfRef
{
    public Lazy<LazySelfRef> Self { get; }
    public LazySelfRef(Lazy<LazySelfRef> self) => Self = self;
}

// Multiple Lazy deps on same type
[Service] public class MultiLazy
{
    public Lazy<SimpleService> Lazy1 { get; }
    public Lazy<ServiceC> Lazy2 { get; }
    public MultiLazy(Lazy<SimpleService> lazy1, Lazy<ServiceC> lazy2)
    { Lazy1 = lazy1; Lazy2 = lazy2; }
}

// Mix of direct and Lazy deps
[Service] public class MixedDeps
{
    public SimpleService Direct { get; }
    public Lazy<ServiceC> Deferred { get; }
    public MixedDeps(SimpleService direct, Lazy<ServiceC> deferred)
    { Direct = direct; Deferred = deferred; }
}

// Lazy in a chain: LazyChainA → LazyChainB → Lazy<LazyChainC>
[Service] public class LazyChainA { public LazyChainB B { get; } public LazyChainA(LazyChainB b) => B = b; }
[Service] public class LazyChainB { public Lazy<LazyChainC> C { get; } public LazyChainB(Lazy<LazyChainC> c) => C = c; }
[Service] public class LazyChainC { }

// Four-way cycle broken by Lazy: Quad1 → Quad2 → Quad3 → Lazy<Quad4> → Quad1
[Service] public class Quad1 { public Quad2 Q { get; } public Quad1(Quad2 q) => Q = q; }
[Service] public class Quad2 { public Quad3 Q { get; } public Quad2(Quad3 q) => Q = q; }
[Service] public class Quad3 { public Lazy<Quad4> Q { get; } public Quad3(Lazy<Quad4> q) => Q = q; }
[Service] public class Quad4 { public Quad1 Q { get; } public Quad4(Quad1 q) => Q = q; }

// Nested Lazy — Lazy<Lazy<T>> (edge case)
[Service] public class NestedLazyConsumer
{
    public Lazy<SimpleService> Inner { get; }
    public NestedLazyConsumer(Lazy<SimpleService> inner) => Inner = inner;
}

// Lazy dep on an unregistered type (should fail when .Value accessed)
public class UnregisteredService { }
[Service] public class LazyUnregistered
{
    public Lazy<UnregisteredService> Dep { get; }
    public LazyUnregistered(Lazy<UnregisteredService> dep) => Dep = dep;
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

    // ── Circular dependency detection (no Lazy — must fail) ─────────────

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

    // ── Three-way circular (no Lazy) — must fail ──────────────────────

    [Fact]
    public void ThreeWayCircularDep_Throws()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            BuildProvider(typeof(TriA)));
        Assert.Contains("Circular dependency detected", ex.Message);
    }

    [Fact]
    public void ThreeWayCircularDep_ErrorContainsAllTypes()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            BuildProvider(typeof(TriA)));
        Assert.Contains("TriA", ex.Message);
        Assert.Contains("TriB", ex.Message);
        Assert.Contains("TriC", ex.Message);
    }

    // ── Lazy<T> — advanced scenarios ──────────────────────────────────

    [Fact]
    public void LazySelfReference_Resolves()
    {
        var (sp, _, _) = BuildProvider(typeof(LazySelfRef));
        var svc = sp.GetRequiredService<LazySelfRef>();
        Assert.NotNull(svc);
        Assert.NotNull(svc.Self);
        // .Value returns the same singleton
        Assert.Same(svc, svc.Self.Value);
    }

    [Fact]
    public void MultipleLazyDeps_Resolve()
    {
        var (sp, _, _) = BuildProvider(typeof(MultiLazy), typeof(SimpleService), typeof(ServiceC));
        var svc = sp.GetRequiredService<MultiLazy>();
        Assert.NotNull(svc.Lazy1);
        Assert.NotNull(svc.Lazy2);
        Assert.IsType<SimpleService>(svc.Lazy1.Value);
        Assert.IsType<ServiceC>(svc.Lazy2.Value);
    }

    [Fact]
    public void MixedDirectAndLazy_Resolves()
    {
        var (sp, _, _) = BuildProvider(typeof(MixedDeps), typeof(SimpleService), typeof(ServiceC));
        var svc = sp.GetRequiredService<MixedDeps>();
        Assert.NotNull(svc.Direct);
        Assert.IsType<SimpleService>(svc.Direct);
        Assert.NotNull(svc.Deferred);
        Assert.IsType<ServiceC>(svc.Deferred.Value);
    }

    [Fact]
    public void LazyInChain_Resolves()
    {
        var (sp, _, _) = BuildProvider(typeof(LazyChainA), typeof(LazyChainB), typeof(LazyChainC));
        var a = sp.GetRequiredService<LazyChainA>();
        Assert.NotNull(a.B);
        Assert.NotNull(a.B.C);
        Assert.IsType<LazyChainC>(a.B.C.Value);
    }

    [Fact]
    public void FourWayCycle_BrokenByLazy()
    {
        var (sp, _, _) = BuildProvider(typeof(Quad1), typeof(Quad2), typeof(Quad3), typeof(Quad4));
        var q1 = sp.GetRequiredService<Quad1>();
        Assert.NotNull(q1.Q);          // Quad2
        Assert.NotNull(q1.Q.Q);        // Quad3
        Assert.NotNull(q1.Q.Q.Q);      // Lazy<Quad4>
        var q4 = q1.Q.Q.Q.Value;       // resolve Lazy
        Assert.NotNull(q4);
        Assert.Same(q1, q4.Q);         // full cycle back to Quad1
    }

    [Fact]
    public void LazyDep_IsNotResolvedUntilValueAccessed()
    {
        // Use Lazy in a cycle context where deferred resolution matters
        var (sp, _, _) = BuildProvider(typeof(LazyA), typeof(LazyB));
        var a = sp.GetRequiredService<LazyA>();
        // Lazy wrapper is created but inner value is deferred
        var lazy = a.B;
        Assert.NotNull(lazy);
        // Accessing .Value resolves it
        var b = lazy.Value;
        Assert.NotNull(b);
        Assert.True(lazy.IsValueCreated);
    }

    [Fact]
    public void LazyDep_ReturnsSameSingletonOnMultipleAccess()
    {
        var (sp, _, _) = BuildProvider(typeof(LazyA), typeof(LazyB));
        var a = sp.GetRequiredService<LazyA>();
        var b1 = a.B.Value;
        var b2 = a.B.Value;
        Assert.Same(b1, b2);
    }

    [Fact]
    public void LazyUnregistered_ConstructsButValueThrows()
    {
        var (sp, _, _) = BuildProvider(typeof(LazyUnregistered));
        var svc = sp.GetRequiredService<LazyUnregistered>();
        // Construction succeeds — Lazy wrapper is created
        Assert.NotNull(svc.Dep);
        Assert.False(svc.Dep.IsValueCreated);
        // Accessing .Value throws because UnregisteredService is not in DI
        Assert.Throws<InvalidOperationException>(() => svc.Dep.Value);
    }

    [Fact]
    public void LazyDep_NestedLazy_Resolves()
    {
        var (sp, _, _) = BuildProvider(typeof(NestedLazyConsumer), typeof(SimpleService));
        var svc = sp.GetRequiredService<NestedLazyConsumer>();
        Assert.NotNull(svc.Inner);
        Assert.IsType<SimpleService>(svc.Inner.Value);
    }

    // ── Circular without Lazy — verify error message quality ──────────

    [Fact]
    public void CircularDep_ErrorMessage_ContainsArrowPath()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            BuildProvider(typeof(CircularX)));
        // Should show path like "CircularX → CircularY → CircularX"
        Assert.Contains("→", ex.Message);
    }

    [Fact]
    public void SelfReference_ErrorMessage_ContainsTypeName()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            BuildProvider(typeof(SelfRef)));
        Assert.Contains("SelfRef", ex.Message);
        Assert.Contains("Circular dependency detected", ex.Message);
    }
}
