using Altruist;
using Altruist.Contracts;
using Altruist.Gaming;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Tests.Gaming.Session;

public sealed class ClientSessionCleanupTests
{
    [Fact]
    public void Configuration_RegistersEachImplementingTypeUnderInterface()
    {
        var services = new ServiceCollection();
        services.AddSingleton<RecordingCleanup>();
        services.AddSingleton<SecondCleanup>();

        // Manually drive the registration helper instead of the full
        // assembly-scanning Configure() — keeps the test independent of
        // whatever IClientSessionCleanup implementations happen to be loaded
        // in the test process (e.g. Valeria's VisibilityHandler).
        ClientSessionCleanupConfiguration.RegisterImplementations(
            services,
            new[] { typeof(RecordingCleanup), typeof(SecondCleanup) });

        using var sp = services.BuildServiceProvider();
        var resolved = sp.GetServices<IClientSessionCleanup>().ToArray();

        Assert.Equal(2, resolved.Length);
        Assert.Contains(resolved, h => h is RecordingCleanup);
        Assert.Contains(resolved, h => h is SecondCleanup);

        // Each IClientSessionCleanup resolution must point at the same singleton
        // instance the rest of the app uses (no double-instantiation).
        Assert.Same(sp.GetRequiredService<RecordingCleanup>(),
            resolved.OfType<RecordingCleanup>().Single());
        Assert.Same(sp.GetRequiredService<SecondCleanup>(),
            resolved.OfType<SecondCleanup>().Single());
    }

    [Fact]
    public async Task Portal_DispatchesCleanupOnDisconnect_ToEveryHandler()
    {
        var first = new RecordingCleanup();
        var second = new SecondCleanup();
        var sessions = new FakeGameSessionService();

        var portal = new TestSessionPortal(
            sessions,
            Mock.Of<IAltruistRouter>(),
            sessionCleanups: new IClientSessionCleanup[] { first, second });

        await portal.OnDisconnectedAsync("client-42", null);

        Assert.Equal(new[] { "client-42" }, first.SeenClients);
        Assert.Equal(new[] { "client-42" }, second.SeenClients);
    }

    [Fact]
    public async Task Portal_FailingHandler_DoesNotBlockOthers()
    {
        var failing = new ThrowingCleanup();
        var second = new RecordingCleanup();
        var sessions = new FakeGameSessionService();

        var portal = new TestSessionPortal(
            sessions,
            Mock.Of<IAltruistRouter>(),
            sessionCleanups: new IClientSessionCleanup[] { failing, second });

        // Must not throw — failing handler is logged and the next one still runs.
        await portal.OnDisconnectedAsync("c1", null);

        Assert.Equal(1, failing.CallCount);
        Assert.Equal(new[] { "c1" }, second.SeenClients);
    }

    // ------------------------- test fakes -------------------------

    private sealed class RecordingCleanup : IClientSessionCleanup
    {
        public List<string> SeenClients { get; } = new();
        public Task Cleanup(string clientId)
        {
            SeenClients.Add(clientId);
            return Task.CompletedTask;
        }
    }

    private sealed class SecondCleanup : IClientSessionCleanup
    {
        public List<string> SeenClients { get; } = new();
        public Task Cleanup(string clientId)
        {
            SeenClients.Add(clientId);
            return Task.CompletedTask;
        }
    }

    private sealed class ThrowingCleanup : IClientSessionCleanup
    {
        public int CallCount { get; private set; }
        public Task Cleanup(string clientId)
        {
            CallCount++;
            throw new InvalidOperationException("boom");
        }
    }

    private sealed class TestSessionPortal : AltruistGameSessionPortal
    {
        public TestSessionPortal(
            IGameSessionService gameSessionService,
            IAltruistRouter router,
            IEnumerable<IClientSessionCleanup>? sessionCleanups = null)
            : base(
                gameSessionService,
                router,
                sessionCleanups ?? Enumerable.Empty<IClientSessionCleanup>(),
                NullLoggerFactory.Instance)
        {
        }
    }

    private sealed class FakeGameSessionService : IGameSessionService
    {
        public IGameSession CreateSession(string sessionId, DateTime expiresAtUtc) =>
            throw new NotSupportedException();

        public IGameSession? GetSession(string sessionId) => null;

        public void ClearSession(string sessionId) { }

        public IGameSession? MigrateSession(string fromSessionId, string toSessionId, DateTime newExpiresAtUtc) =>
            throw new NotSupportedException();

        public Task Cleanup() => Task.CompletedTask;

        public IEnumerable<T> FindAllContexts<T>() => Enumerable.Empty<T>();
        public IEnumerable<T> FindAllContexts<T>(string id) => Enumerable.Empty<T>();
        public IEnumerable<object> FindAllContexsts(string sessionId) => Enumerable.Empty<object>();
        public IEnumerable<object> FindContexts(string id, params Type[] types) => Enumerable.Empty<object>();

        public Task<RoomBroadcast?> ExitGameAsync(LeaveGamePacket message, string clientId) =>
            throw new NotSupportedException();

        public Task<IResultPacket> JoinGameAsync(JoinGamePacket message, string clientId) =>
            throw new NotSupportedException();

        public Task<IResultPacket> HandshakeAsync(HandshakeRequestPacket message, string clientId) =>
            throw new NotSupportedException();
    }
}
