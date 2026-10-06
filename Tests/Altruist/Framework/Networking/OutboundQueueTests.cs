/*
Copyright 2025 Aron Gere
Licensed under the Apache License, Version 2.0
*/

using Altruist;
using Altruist.Codec.MessagePack;

using MessagePack;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using Moq;

namespace Tests.Altruist.Framework.Networking;

/// <summary>
/// The per-client outbound queues (0.9.9): ClientSender.Enqueue / CloseAfterFlush / Forget, packet
/// coalescing, slow-reader aborts, queued mode, metrics, the shared instance in DI and the
/// connection manager forgetting a closed connection.
/// </summary>
// Wall-clock timings: run alone, not next to the CPU-heavy tests.
[Collection(Tests.Gaming.Engine.CpuHeavyCollection.Name)]
public sealed class OutboundQueueTests
{
    [MessagePackObject]
    public sealed class Hello : IPacketBase
    {
        [Key(0)] public uint MessageCode { get; set; } = 501;
        [Key(1)] public int Value { get; set; }
    }

    [MessagePackObject]
    [Coalesce("state")]
    public sealed class State : IPacketBase
    {
        [Key(0)] public uint MessageCode { get; set; } = 502;
        [Key(1)] public int Tick { get; set; }
    }

    [MessagePackObject]
    [Coalesce("state")]
    public sealed class OtherState : IPacketBase
    {
        [Key(0)] public uint MessageCode { get; set; } = 503;
        [Key(1)] public int Tick { get; set; }
    }

    [MessagePackObject]
    public sealed class Bye : IPacketBase
    {
        [Key(0)] public uint MessageCode { get; set; } = 504;
    }

    /// <summary>A socket whose sends can be held open to simulate a client that stops reading.</summary>
    private sealed class FakeConnection : AltruistConnection
    {
        public readonly List<object[]> Sent = new();
        public readonly List<string> Log = new();
        public TaskCompletionSource? Gate;
        public readonly TaskCompletionSource InFlight = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public volatile bool Aborted;
        public volatile bool Open = true;
        public int Concurrent;
        public int MaxConcurrent;

        public override bool IsConnected => Open && !Aborted;

        public override async Task SendAsync(byte[] data)
        {
            var now = Interlocked.Increment(ref Concurrent);
            MaxConcurrent = Math.Max(MaxConcurrent, now);
            InFlight.TrySetResult();
            if (Gate is { } g)
                await g.Task;
            var env = MessagePackSerializer.Deserialize<object[]>(data);
            lock (Sent)
            {
                Sent.Add(env);
                Log.Add($"send {env[0]}");
            }
            Interlocked.Decrement(ref Concurrent);
        }

        public override Task CloseOutputAsync()
        {
            lock (Sent)
                Log.Add("close");
            Open = false;
            return Task.CompletedTask;
        }

        public override void Abort() => Aborted = true;
    }

    private sealed class CountingMetrics : IOutboundMetrics
    {
        public int Depth, MaxClientLength, Coalesced, SlowClosed, Sent, Bytes, Errors;
        public void QueueDepthChanged(int delta) => Interlocked.Add(ref Depth, delta);
        public void ClientQueueLength(int length) => MaxClientLength = Math.Max(MaxClientLength, length);
        void IOutboundMetrics.Coalesced() => Interlocked.Increment(ref Coalesced);
        public void SlowClientClosed() => Interlocked.Increment(ref SlowClosed);
        void IOutboundMetrics.Sent(int bytes) { Interlocked.Increment(ref Sent); Interlocked.Add(ref Bytes, bytes); }
        public void SendError() => Interlocked.Increment(ref Errors);
    }

    private static IConnectionStore StoreOf(FakeConnection conn)
    {
        var store = new Mock<IConnectionStore>();
        store.Setup(s => s.GetConnectionAsync("c")).ReturnsAsync(conn);
        return store.Object;
    }

    private static (ClientSender Sender, FakeConnection Conn, CountingMetrics Metrics) Make(
        string mode = "direct", double stuckSeconds = 5, int maxQueued = 256)
    {
        var conn = new FakeConnection();
        var metrics = new CountingMetrics();
        var store = StoreOf(conn);
        var queues = new OutboundQueues(store, new MessagePackCodec(), NullLoggerFactory.Instance, metrics,
            mode: mode, maxQueuedPerClient: maxQueued, stuckSendSeconds: stuckSeconds);
        return (new ClientSender(store, new MessagePackCodec(), outbound: queues), conn, metrics);
    }

    private static async Task Until(Func<bool> cond)
    {
        for (var i = 0; i < 1000 && !cond(); i++)
            await Task.Delay(5);
        Assert.True(cond());
    }

    private static uint Code(object[] envelope) => Convert.ToUInt32(envelope[0]);

    private static int Field(object[] envelope, int index) => Convert.ToInt32(((object[])envelope[2])[index]);

    [Fact]
    public async Task Enqueued_packets_are_sent_in_order_as_message_envelopes()
    {
        var (sender, conn, metrics) = Make();
        sender.Enqueue("c", new Hello { Value = 3 });
        sender.Enqueue("c", new State { Tick = 1 });
        sender.Enqueue("c", new Bye());
        await Until(() => conn.Sent.Count == 3);

        Assert.Equal(new uint[] { 501, 502, 504 }, conn.Sent.Select(Code));
        // [code, header, packetArray]: the packet array starts with its own code.
        Assert.Equal(501u, Convert.ToUInt32(((object[])conn.Sent[0][2])[0]));
        Assert.Equal(3, Field(conn.Sent[0], 1));
        Assert.Equal(1, conn.MaxConcurrent);
        await Until(() => metrics.Sent == 3);
        Assert.True(metrics.Bytes > 0);
        Assert.Equal(0, metrics.Depth);
    }

    [Fact]
    public async Task A_blocked_client_keeps_only_the_newest_coalesced_packet_and_every_other_packet()
    {
        var (sender, conn, metrics) = Make();
        conn.Gate = new TaskCompletionSource();
        sender.Enqueue("c", new State { Tick = 1 }); // in flight, held by the gate
        await conn.InFlight.Task.WaitAsync(TimeSpan.FromSeconds(5));
        sender.Enqueue("c", new State { Tick = 2 });
        sender.Enqueue("c", new Hello());
        sender.Enqueue("c", new OtherState { Tick = 3 }); // same key: supersedes tick 2
        sender.Enqueue("c", new Bye());
        sender.Enqueue("c", new State { Tick = 4 });
        conn.Gate.SetResult();
        await Until(() => conn.Sent.Count == 4);
        await Task.Delay(30);

        Assert.Equal(new uint[] { 502, 501, 504, 502 }, conn.Sent.Select(Code));
        Assert.Equal(new[] { 1, 4 }, conn.Sent.Where(e => Code(e) == 502).Select(e => Field(e, 1)));
        Assert.Equal(2, metrics.Coalesced);
    }

    [Fact]
    public async Task Close_after_flush_happens_after_the_packets_queued_before_it()
    {
        var (sender, conn, _) = Make();
        sender.Enqueue("c", new Bye());
        sender.CloseAfterFlush("c");
        sender.Enqueue("c", new Hello()); // after the close: the socket is no longer open
        await Until(() => conn.Log.Contains("close"));
        await Task.Delay(30);

        Assert.Equal(new[] { "send 504", "close" }, conn.Log);
    }

    [Fact]
    public async Task A_send_stuck_past_the_limit_aborts_the_connection()
    {
        var (sender, conn, metrics) = Make(stuckSeconds: 0.05);
        conn.Gate = new TaskCompletionSource();
        sender.Enqueue("c", new State { Tick = 1 });
        await Task.Delay(120);
        Assert.False(conn.Aborted);
        sender.Enqueue("c", new State { Tick = 2 }); // the next enqueue notices the stuck send
        await Until(() => conn.Aborted);
        // Nothing more is queued for an aborted client.
        sender.Enqueue("c", new Hello());
        conn.Gate.SetResult();
        await Task.Delay(50);

        Assert.Single(conn.Sent);
        Assert.Equal(1, metrics.SlowClosed);
        Assert.Equal(0, metrics.Depth);
    }

    [Fact]
    public async Task Too_many_queued_packets_abort_the_connection()
    {
        var (sender, conn, metrics) = Make(maxQueued: 256);
        conn.Gate = new TaskCompletionSource();
        for (var i = 0; i <= 256 + 1; i++)
            sender.Enqueue("c", new Hello { Value = i });
        await Until(() => conn.Aborted);
        conn.Gate.SetResult();

        Assert.Equal(1, metrics.SlowClosed);
        Assert.True(metrics.MaxClientLength > 256);
    }

    [Fact]
    public async Task Forget_drops_whatever_is_still_queued()
    {
        var (sender, conn, metrics) = Make();
        conn.Gate = new TaskCompletionSource();
        sender.Enqueue("c", new Hello());
        await conn.InFlight.Task.WaitAsync(TimeSpan.FromSeconds(5));
        sender.Enqueue("c", new Hello());
        sender.Enqueue("c", new Hello());
        sender.Forget("c");
        conn.Gate.SetResult();
        await Task.Delay(50);

        Assert.Single(conn.Sent);
        Assert.Equal(0, metrics.Depth);
        Assert.Equal(0, sender.Outbound.ClientCount);
    }

    [Fact]
    public async Task Direct_mode_is_the_default_and_SendAsync_awaits_the_socket()
    {
        var conn = new FakeConnection { Gate = new TaskCompletionSource() };
        var sender = new ClientSender(StoreOf(conn), new MessagePackCodec());
        Assert.Equal(OutboundMode.Direct, sender.Mode);

        var send = sender.SendAsync("c", new Hello());
        await conn.InFlight.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(send.IsCompleted);
        conn.Gate.SetResult();
        await send;
        Assert.Single(conn.Sent);
        // A hand-made sender still queues on Enqueue (its own queues, default limits).
        sender.Enqueue("c", new Bye());
        await Until(() => conn.Sent.Count == 2);
    }

    [Fact]
    public async Task Queued_mode_SendAsync_returns_at_once_and_coalesces()
    {
        var (sender, conn, _) = Make(mode: "queued");
        Assert.Equal(OutboundMode.Queued, sender.Mode);
        conn.Gate = new TaskCompletionSource();

        var first = sender.SendAsync("c", new State { Tick = 1 });
        Assert.True(first.IsCompleted);
        await conn.InFlight.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await sender.SendAsync("c", new State { Tick = 2 });
        await sender.SendAsync("c", new State { Tick = 3 });
        await sender.SendAsync("c", MessagePackSerializer.Serialize(new object[] { 9 })); // raw bytes keep their place
        conn.Gate.SetResult();
        await Until(() => conn.Sent.Count == 3);

        Assert.Equal(new[] { 1, 3 }, conn.Sent.Where(e => e.Length == 3).Select(e => Field(e, 1)));
        Assert.Equal(9, Convert.ToInt32(conn.Sent[2][0]));
    }

    [Fact]
    public void Unknown_modes_are_rejected_and_coalesce_keys_are_read_from_the_attribute()
    {
        Assert.Equal(OutboundMode.Direct, OutboundQueues.ParseMode(null));
        Assert.Equal(OutboundMode.Queued, OutboundQueues.ParseMode("Queued"));
        Assert.Throws<ArgumentException>(() => OutboundQueues.ParseMode("later"));
        Assert.Equal("state", OutboundQueues.CoalesceKeyOf(typeof(State)));
        Assert.Null(OutboundQueues.CoalesceKeyOf(typeof(Hello)));
    }

    [Fact]
    public void Config_keys_reach_the_queues_and_every_sender_from_DI_shares_one_instance()
    {
        var cfg = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["altruist:server:transport:codec:provider"] = "messagepack",
            ["altruist:server:transport:outbound:mode"] = "queued",
            ["altruist:server:transport:outbound:max-queued-per-client"] = "64",
            ["altruist:server:transport:outbound:stuck-send-seconds"] = "2.5",
            ["altruist:server:transport:outbound:encode-buffer-bytes"] = "4096",
        }).Build();
        var services = new ServiceCollection();
        services.AddLogging(b => b.ClearProviders());
        services.AddSingleton<IConfiguration>(cfg);
        services.AddSingleton(new Mock<IConnectionStore>().Object);
        services.AddSingleton<ICodec>(new MessagePackCodec());
        DependencyResolver.EnsureConverters(services, cfg, NullLogger.Instance);
        var reg = new List<string>();
        foreach (var t in new[] { typeof(OutboundQueues), typeof(ClientSender) })
            AltruistDIServiceConfig.RegisterServiceType(services, cfg, NullLogger.Instance, reg, t);
        using var sp = services.BuildServiceProvider();

        var queues = sp.GetRequiredService<OutboundQueues>();
        Assert.Equal(OutboundMode.Queued, queues.Mode);
        Assert.Equal(64, queues.MaxQueuedPerClient);
        Assert.Equal(TimeSpan.FromSeconds(2.5), queues.StuckSendLimit);
        Assert.Equal(4096, queues.EncodeBufferBytes);
        var sender = sp.GetRequiredService<ClientSender>();
        Assert.Same(queues, sender.Outbound);
        Assert.Equal(OutboundMode.Queued, sender.Mode);
    }

    [Fact]
    public async Task The_connection_manager_forgets_the_queue_of_a_closed_connection()
    {
        var conn = new FakeConnection { Gate = new TaskCompletionSource() };
        var store = StoreOf(conn);
        var queues = new OutboundQueues(store, new MessagePackCodec());
        var codecs = new Mock<ICodecResolver>();
        codecs.Setup(c => c.Resolve(It.IsAny<string?>())).Returns(new MessagePackCodec());
        var manager = new ConnectionManager(new Mock<ISocketManager>().Object, codecs.Object, NullLoggerFactory.Instance, outbound: queues);

        queues.Enqueue("c", new Hello());
        await conn.InFlight.Task.WaitAsync(TimeSpan.FromSeconds(5));
        queues.Enqueue("c", new Hello());
        Assert.Equal(1, queues.ClientCount);
        await manager.DisconnectAsync("c");
        conn.Gate.SetResult();
        await Task.Delay(50);

        Assert.Equal(0, queues.ClientCount);
        Assert.Single(conn.Sent);
    }
}
