/*
Copyright 2025 Aron Gere
Licensed under the Apache License, Version 2.0
*/

using System.Reflection;

using Altruist.Persistence.Postgres;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

using Moq;

using Npgsql;

using Xunit.Abstractions;
using Xunit.Sdk;

namespace Altruist.Testing.Internal;

/// <summary>
/// Per-test xUnit class runner for <see cref="AltruistTestAttribute"/> classes.
///
/// <para><b>Per-class schema isolation.</b> Each test class gets its own Postgres
/// schema named <c>test_&lt;classname&gt;</c>. The schema is dropped + recreated
/// at <see cref="AfterTestClassStartingAsync"/> (so leftover rows from a previous
/// run never affect this run), all <c>[Vault]</c> tables are cloned into it via
/// <c>CREATE TABLE … (LIKE prod.tbl INCLUDING ALL)</c>, and at
/// <see cref="BeforeTestClassFinishedAsync"/> the schema is dropped — unless
/// <c>[AltruistTest(KeepSchema = true)]</c> for post-mortem inspection.</para>
///
/// <para><b>Per-method DI freshness.</b> Each <c>[Fact]</c> gets a freshly built
/// <see cref="IServiceProvider"/> with mocks reset and singleton state empty.
/// The per-method provider replaces the Postgres <c>IServiceFactory</c> with
/// a <c>TestPostgresServiceFactory</c> bound to the per-class schema, so
/// every <see cref="IVault{T}"/> resolution targets the test schema regardless of
/// the vault model's declared keyspace. The provider is also pushed onto
/// <see cref="Dependencies"/>'s AsyncLocal scope so production code paths that
/// resolve services via <c>Dependencies.Inject&lt;T&gt;</c> (notably
/// <c>PgVaultQuery.From&lt;T&gt;</c>) hit the per-test container too.</para>
///
/// <para><b>Mock visibility.</b> Each <see cref="MockAttribute"/>-tagged constructor
/// parameter is replaced per method with a fresh <see cref="Mock{T}"/>; the mock is
/// registered in the per-method container before sibling services are constructed,
/// matching Spring <c>@MockBean</c> semantics.</para>
/// </summary>
internal sealed class AltruistClassRunner : XunitTestClassRunner
{
    private readonly bool _altruistManaged;
    private readonly AltruistTestAttribute? _attr;
    private readonly bool _isIntegration;
    private readonly List<MockSpec> _mockSpecs = new();
    private readonly string _testSchema;
    private IServiceProvider? _currentProvider;
    private IDisposable? _currentScope;

    private readonly record struct MockSpec(Type ServiceType, MockBehavior Behavior);

    public AltruistClassRunner(
        ITestClass testClass,
        IReflectionTypeInfo @class,
        IEnumerable<IXunitTestCase> testCases,
        IMessageSink diagnosticMessageSink,
        IMessageBus messageBus,
        ITestCaseOrderer testCaseOrderer,
        ExceptionAggregator aggregator,
        CancellationTokenSource cancellationTokenSource,
        IDictionary<Type, object> collectionFixtureMappings)
        : base(testClass, @class, testCases, diagnosticMessageSink, messageBus, testCaseOrderer, aggregator, cancellationTokenSource, collectionFixtureMappings)
    {
        _attr = @class.Type.GetCustomAttribute<AltruistTestAttribute>(inherit: true);
        _isIntegration = @class.Type.GetCustomAttribute<AltruistIntegrationTestAttribute>(inherit: true) is not null;
        _altruistManaged = _attr is not null;
        _testSchema = SchemaIsolation.SchemaNameFor(@class.Type);
    }

    /// <summary>
    /// Discover [Mock] params and ensure a fresh per-class schema exists before any
    /// [Fact] runs. Schema lifetime spans the whole class; per-method provider
    /// rebuilds happen inside it.
    ///
    /// <para>For <c>[AltruistIntegrationTest]</c> classes, just bring up the live
    /// process-shared server; tests inherit from <c>IntegrationTestBase</c> and
    /// drive the server via TCP/HTTP — no per-method DI rebuild needed.</para>
    /// </summary>
    protected override async Task AfterTestClassStartingAsync()
    {
        await base.AfterTestClassStartingAsync();

        if (_isIntegration)
        {
            await LiveServerHandle.EnsureStartedAsync();
            return;
        }

        if (!_altruistManaged) return;

        if (_attr!.RunModuleLoaders)
            AltruistTestRuntime.EnsureModulesRun();

        var ctor = SelectAltruistConstructor(Class.Type);
        if (ctor is not null)
        {
            foreach (var p in ctor.GetParameters())
            {
                var mockAttr = p.GetCustomAttribute<MockAttribute>();
                if (mockAttr is null) continue;
                _mockSpecs.Add(new MockSpec(
                    p.ParameterType,
                    mockAttr.Strict ? MockBehavior.Strict : MockBehavior.Loose));
            }
        }

        // Postgres-only path. If the configured provider isn't Postgres, skip
        // schema setup — vault tests can't run against this provider anyway, and
        // pure-DI tests (no vault injection) don't need the isolation step.
        var dataSource = AltruistTestRuntime.Root.GetService<NpgsqlDataSource>();
        if (dataSource is not null)
            await SchemaIsolation.EnsureFreshSchemaAsync(dataSource, _testSchema);
    }

    /// <summary>
    /// xUnit's class runner builds ctor args ONCE per class and reuses them for
    /// every method group. We override <see cref="RunTestMethodAsync"/> below to
    /// recompute args per <c>[Fact]</c> via this method, so each test sees fresh
    /// singletons + mocks.
    /// </summary>
    protected override object[] CreateTestClassConstructorArguments()
    {
        // Integration mode: tests inherit from IntegrationTestBase, take only
        // ITestOutputHelper. Defer to xUnit's default arg resolution; no DI rebuild.
        if (_isIntegration)
            return base.CreateTestClassConstructorArguments();

        if (!_altruistManaged)
            return base.CreateTestClassConstructorArguments();

        DisposeProvider();
        _currentProvider = BuildPerMethodProvider();
        _currentScope = Dependencies.PushScope(_currentProvider);
        var args = base.CreateTestClassConstructorArguments();
        return args;
    }

    /// <summary>
    /// xUnit's base implementation passes the per-class <paramref name="constructorArguments"/>
    /// here. We discard it and rebuild fresh args (which rebuilds the per-method provider
    /// via <see cref="CreateTestClassConstructorArguments"/>) so each <c>[Fact]</c> in
    /// the class is fully isolated.
    /// </summary>
    protected override Task<RunSummary> RunTestMethodAsync(
        ITestMethod testMethod,
        IReflectionMethodInfo method,
        IEnumerable<IXunitTestCase> testCases,
        object[] constructorArguments)
    {
        // Both [AltruistIntegrationTest] and [AltruistTest] need fresh ctor args
        // per *test case* (every [Fact] AND every [Theory]+[InlineData] row):
        // integration mode because the injected test clients are disposable and
        // would carry state from the previous case; AltruistTest mode because
        // each test wants a fresh DI provider.
        //
        // The base xUnit method runner caches one set of ctor args for a whole
        // method group, so we route through AltruistTestMethodRunner which
        // rebuilds args per RunTestCaseAsync call.
        if (_isIntegration || _altruistManaged)
        {
            return new AltruistTestMethodRunner(
                CreateTestClassConstructorArguments,
                testMethod,
                Class,
                method,
                testCases,
                DiagnosticMessageSink,
                MessageBus,
                new ExceptionAggregator(Aggregator),
                CancellationTokenSource,
                constructorArguments
            ).RunAsync();
        }

        return base.RunTestMethodAsync(testMethod, method, testCases, constructorArguments);
    }

    private IServiceProvider BuildPerMethodProvider()
    {
        IServiceCollection services = new ServiceCollection();

        // Clone every root descriptor. Per-provider singletons mean each per-method
        // container builds its own fresh instances — no cross-test state leak.
        // Overrides (mocks, vault-schema) are added below and win because MEDI
        // returns the last matching descriptor for a service type.
        foreach (var d in AltruistTestRuntime.RootDescriptors)
            services.Add(d);

        // Override every IVault<T> to target the per-class test schema.
        TestVaultRegistration.RegisterTestVaults(services, _testSchema);

        foreach (var spec in _mockSpecs)
        {
            var mockType = typeof(Mock<>).MakeGenericType(spec.ServiceType);
            var mock = (Mock)Activator.CreateInstance(mockType, spec.Behavior)!;

            for (int i = services.Count - 1; i >= 0; i--)
                if (services[i].ServiceType == spec.ServiceType)
                    services.RemoveAt(i);
            services.AddSingleton(spec.ServiceType, mock.Object);
        }

        var provider = services.BuildServiceProvider();

        // Re-run [AltruistModuleLoader] static methods against THIS provider so
        // data registries populated at boot (item templates, mob registries, etc.)
        // are available on the per-method instances. Skip for pure-formula tests
        // that don't need them — saves ~1–2s per [Fact].
        if (_attr!.RunModuleLoaders)
            AltruistModuleConfig.RunModulesAsync(provider).GetAwaiter().GetResult();

        return provider;
    }

    /// <summary>
    /// xUnit class fixtures and <c>ITestOutputHelper</c> use the base class's
    /// own injection path — we defer to it first, then fall back to DI for
    /// anything it can't resolve.
    ///
    /// <para>For <c>[AltruistIntegrationTest]</c> classes, we recognize the
    /// framework-provided test client types (<see cref="TestHttpClient"/>,
    /// <see cref="TestTcpClient"/>, etc.) and produce a fresh instance per
    /// constructor parameter — so a multi-client test (e.g. PartyTest with
    /// two TCP sessions) just takes two parameters of the same type.</para>
    /// </summary>
    protected override bool TryGetConstructorArgument(
        ConstructorInfo constructor,
        int index,
        ParameterInfo parameter,
        out object argumentValue)
    {
        if (base.TryGetConstructorArgument(constructor, index, parameter, out argumentValue))
            return true;

        if (_isIntegration)
        {
            if (TryResolveIntegrationClient(parameter.ParameterType, out var client))
            {
                argumentValue = client;
                return true;
            }

            // Fall through to the live server's DI container so tests can inject
            // any production service — IVault<T>, ICharacterService, etc. — and
            // assert against real persisted state without the manual SQL shim.
            var live = LiveServerHandle.Provider.GetService(parameter.ParameterType);
            if (live is not null)
            {
                argumentValue = live;
                return true;
            }
            argumentValue = null!;
            return false;
        }

        if (_currentProvider is null)
        {
            argumentValue = null!;
            return false;
        }

        var resolved = _currentProvider.GetService(parameter.ParameterType);
        if (resolved is null)
        {
            argumentValue = null!;
            return false;
        }
        argumentValue = resolved;
        return true;
    }

    /// <summary>Build a fresh test-client instance for an
    /// <c>[AltruistIntegrationTest]</c> ctor param. Each call returns a NEW
    /// object so multi-client tests (two TCP sessions, two HTTP clients) get
    /// independent state.</summary>
    private static bool TryResolveIntegrationClient(Type type, out object client)
    {
        var cfg = LiveServerHandle.Provider.GetRequiredService<IConfiguration>();
        if (type == typeof(TestHttpClient))      { client = new TestHttpClient(cfg);      return true; }
        if (type == typeof(TestTcpClient))       { client = new TestTcpClient(cfg);       return true; }
        if (type == typeof(TestUdpClient))       { client = new TestUdpClient(cfg);       return true; }
        if (type == typeof(TestWebSocketClient)) { client = new TestWebSocketClient(cfg); return true; }
        if (type == typeof(TestPlayerSession))   { client = new TestPlayerSession(cfg);   return true; }
        client = null!;
        return false;
    }

    protected override async Task BeforeTestClassFinishedAsync()
    {
        try
        {
            await base.BeforeTestClassFinishedAsync();
        }
        finally
        {
            DisposeProvider();

            if (_altruistManaged && _attr!.KeepSchema == false)
            {
                var dataSource = AltruistTestRuntime.Root.GetService<NpgsqlDataSource>();
                if (dataSource is not null)
                {
                    try { await SchemaIsolation.DropSchemaAsync(dataSource, _testSchema); }
                    catch { /* best-effort cleanup; schema may not exist if setup never ran */ }
                }
            }
        }
    }

    private void DisposeProvider()
    {
        _currentScope?.Dispose();
        _currentScope = null;
        (_currentProvider as IDisposable)?.Dispose();
        _currentProvider = null;
    }

    /// <summary>
    /// Pick the first public constructor (xUnit's own selection logic).
    /// </summary>
    private static ConstructorInfo? SelectAltruistConstructor(Type classType)
        => classType.GetConstructors(BindingFlags.Public | BindingFlags.Instance).FirstOrDefault();
}
