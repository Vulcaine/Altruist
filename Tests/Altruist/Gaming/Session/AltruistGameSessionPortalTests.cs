using Altruist;
using Altruist.Gaming;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Tests.Gaming.Session;

public sealed class AltruistGameSessionPortalTests
{
    [Fact]
    public async Task OnConnectedAsync_CreatesSessionBeforeCallingHook()
    {
        var sessions = new FakeGameSessionService();
        var portal = new TestSessionPortal(sessions, Mock.Of<IAltruistRouter>());

        await portal.OnConnectedAsync("client-1", null!, null!);

        Assert.NotNull(sessions.GetSession("client-1"));
        Assert.NotNull(portal.ConnectedSession);
        Assert.Equal("client-1", portal.ConnectedSession.Id);
    }

    [Fact]
    public async Task OnDisconnectedAsync_RunsHookThenClearSession()
    {
        var order = new List<string>();
        var sessions = new FakeGameSessionService(order);
        sessions.CreateSession("client-1", DateTime.UtcNow.AddMinutes(1));

        var portal = new TestSessionPortal(
            sessions,
            Mock.Of<IAltruistRouter>(),
            () => order.Add("hook"));

        await portal.OnDisconnectedAsync("client-1", null);

        Assert.Equal(["hook", "clear"], order);
        Assert.Null(sessions.GetSession("client-1"));
    }

    private sealed class TestSessionPortal : AltruistGameSessionPortal
    {
        private readonly Action? _disconnecting;

        public IGameSession? ConnectedSession { get; private set; }

        public TestSessionPortal(
            IGameSessionService gameSessionService,
            IAltruistRouter router,
            Action? disconnecting = null,
            IEnumerable<IClientSessionCleanup>? sessionCleanups = null)
            : base(gameSessionService, router, sessionCleanups ?? Enumerable.Empty<IClientSessionCleanup>(), NullLoggerFactory.Instance)
        {
            _disconnecting = disconnecting;
        }

        protected override Task OnSessionConnectedAsync(
            string clientId,
            IGameSession session,
            ConnectionManager connectionManager,
            AltruistConnection connection)
        {
            ConnectedSession = session;
            return Task.CompletedTask;
        }

        protected override Task OnSessionDisconnectingAsync(string clientId, IGameSession? session, Exception? exception)
        {
            _disconnecting?.Invoke();
            return Task.CompletedTask;
        }
    }

    private sealed class FakeGameSessionService : IGameSessionService
    {
        private readonly Dictionary<string, FakeGameSession> _sessions = new();
        private readonly List<string>? _order;

        public FakeGameSessionService(List<string>? order = null)
        {
            _order = order;
        }

        public IGameSession CreateSession(string sessionId, DateTime expiresAtUtc)
        {
            if (!_sessions.TryGetValue(sessionId, out var session))
            {
                session = new FakeGameSession(sessionId, expiresAtUtc);
                _sessions[sessionId] = session;
            }
            else
            {
                session.Renew(expiresAtUtc);
            }

            return session;
        }

        public IGameSession? GetSession(string sessionId) =>
            _sessions.TryGetValue(sessionId, out var session) ? session : null;

        public void ClearSession(string sessionId)
        {
            if (_sessions.Remove(sessionId))
                _order?.Add("clear");
        }

        public IGameSession? MigrateSession(string fromSessionId, string toSessionId, DateTime newExpiresAtUtc) =>
            throw new NotSupportedException();

        public Task Cleanup() => Task.CompletedTask;

        public IEnumerable<T> FindAllContexts<T>() => _sessions.Values.SelectMany(s => s.FindAllContexts<T>());

        public IEnumerable<T> FindAllContexts<T>(string id) =>
            GetSession(id)?.FindAllContexts<T>() ?? Enumerable.Empty<T>();

        public IEnumerable<object> FindAllContexsts(string sessionId) =>
            GetSession(sessionId)?.FindAllContexts() ?? Enumerable.Empty<object>();

        public IEnumerable<object> FindContexts(string id, params Type[] types) =>
            GetSession(id)?.FindContexts(types) ?? Enumerable.Empty<object>();

        public Task<RoomBroadcast?> ExitGameAsync(LeaveGamePacket message, string clientId) =>
            throw new NotSupportedException();

        public Task<IResultPacket> JoinGameAsync(JoinGamePacket message, string clientId) =>
            throw new NotSupportedException();

        public Task<IResultPacket> HandshakeAsync(HandshakeRequestPacket message, string clientId) =>
            throw new NotSupportedException();
    }

    private sealed class FakeGameSession : IGameSession
    {
        private readonly Dictionary<string, List<object>> _contexts = new();

        public FakeGameSession(string id, DateTime expiresAtUtc)
        {
            Id = id;
            ExpiresAtUtc = expiresAtUtc;
        }

        public string Id { get; }
        public DateTime ExpiresAtUtc { get; private set; }

        public void Renew(DateTime newExpiresAtUtc) => ExpiresAtUtc = newExpiresAtUtc;
        public bool Expired() => DateTime.UtcNow >= ExpiresAtUtc;

        public void SetContext<T>(string id, T value)
        {
            if (!_contexts.TryGetValue(id, out var list))
            {
                list = new List<object>();
                _contexts[id] = list;
            }

            list.RemoveAll(v => v is T);
            list.Add(value!);
        }

        public T? GetContext<T>(string id)
        {
            return _contexts.TryGetValue(id, out var list)
                ? list.OfType<T>().LastOrDefault()
                : default;
        }

        public IEnumerable<T> FindAllContexts<T>() => _contexts.Values.SelectMany(v => v).OfType<T>().ToList();

        public IEnumerable<object> FindContexts(params Type[] types)
        {
            return _contexts.Values
                .SelectMany(v => v)
                .Where(v => types.Length == 0 || types.Any(t => t.IsInstanceOfType(v)))
                .ToList();
        }

        public IEnumerable<object> FindAllContexts() => _contexts.Values.SelectMany(v => v).ToList();

        public void RemoveContext<T>(string id)
        {
            if (_contexts.TryGetValue(id, out var list))
                list.RemoveAll(v => v is T);
        }

        public void ClearAllContexts() => _contexts.Clear();
    }
}
