/*
Copyright 2025 Aron Gere
Licensed under the Apache License, Version 2.0
*/

using System.Diagnostics;
using System.Net;
using System.Text.Json;

using Altruist;
using Altruist.Transport;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

using Moq;

using Tests.Altruist.Framework.Scaling;

namespace Tests.Altruist.Config;

/// <summary>
/// The server node on a real Kestrel host: the probe and capacity endpoints, the drain endpoint
/// and its guard, and the drain that runs when the host stops.
/// </summary>
public sealed class ServerNodeEndpointTests : IAsyncLifetime
{
    private readonly FakeParticipant _rooms = new("rooms");
    private readonly FakeContributor _load = new("rooms", 3, 3);
    private ServerNode _node = null!;
    private WebApplication _app = null!;
    private HttpClient _http = null!;
    private ServiceProvider? _bootstrap;
    private bool _stopped;

    public async Task InitializeAsync()
    {
        _node = new ServerNode(new ServerNodeOptions
        {
            MaxLoad = 10,
            DrainTimeout = TimeSpan.FromSeconds(20),
            ForceStopGrace = TimeSpan.FromSeconds(1),
            PollInterval = TimeSpan.FromMilliseconds(5),
        }, new[] { _load }, new[] { _rooms }, () => ReadyState.Alive, "endpoint-node");

        var root = new ServiceCollection();
        root.AddSingleton(new MutableConfigSource(new MutableConfigProvider()));
        root.AddSingleton<IServerNode>(_node);
        root.AddSingleton<IHostedService>(new ServerDrainOnShutdown(_node));
        var status = new Mock<IServerStatus>();
        status.Setup(s => s.Status).Returns(ReadyState.Alive);
        root.AddSingleton(status.Object);
        var context = new Mock<IAltruistContext>();
        context.SetupProperty(c => c.ServerInfo);
        context.Setup(c => c.Endpoints).Returns(new HashSet<string>());

        var bootstrap = _bootstrap = root.BuildServiceProvider();
        var startup = new AltruistStartupConfiguration(
            new ApplicationArgs(), "127.0.0.1", "0", "/", "/ws",
            NullLoggerFactory.Instance, context.Object, status.Object, Array.Empty<ITransport>());
        _app = (await startup.BuildAndStartAsync(root, bootstrap))!;
        var address = _app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        _http = new HttpClient { BaseAddress = new Uri(address) };
    }

    public async Task DisposeAsync()
    {
        _http.Dispose();
        _rooms.Drained = true; // let a pending shutdown drain finish
        if (!_stopped) await _app.StopAsync();
        await _app.DisposeAsync();
        if (_bootstrap is not null) await _bootstrap.DisposeAsync();
        _node.Dispose();
    }

    private async Task<(HttpStatusCode Status, JsonElement Body)> Get(string path)
    {
        using var res = await _http.GetAsync(path);
        var text = await res.Content.ReadAsStringAsync();
        return (res.StatusCode, JsonDocument.Parse(text).RootElement.Clone());
    }

    [Fact]
    public async Task Live_ready_and_capacity_report_the_node()
    {
        var (liveStatus, live) = await Get("/altruist/server/live");
        Assert.Equal(HttpStatusCode.OK, liveStatus);
        Assert.Equal("endpoint-node", live.GetProperty("node").GetString());

        var (readyStatus, ready) = await Get("/altruist/server/ready");
        Assert.Equal(HttpStatusCode.OK, readyStatus);
        Assert.Equal("Ready", ready.GetProperty("state").GetString());

        var (capStatus, cap) = await Get("/altruist/server/capacity");
        Assert.Equal(HttpStatusCode.OK, capStatus);
        Assert.Equal("endpoint-node", cap.GetProperty("nodeId").GetString());
        Assert.Equal("Ready", cap.GetProperty("state").GetString());
        Assert.Equal(3, cap.GetProperty("load").GetDouble());
        Assert.Equal(10, cap.GetProperty("maxLoad").GetDouble());
        Assert.Equal(7, cap.GetProperty("free").GetDouble());
        var kind = Assert.Single(cap.GetProperty("kinds").EnumerateArray());
        Assert.Equal("rooms", kind.GetProperty("kind").GetString());
        Assert.Equal(3, kind.GetProperty("units").GetInt32());
    }

    [Fact]
    public async Task A_full_node_is_not_ready()
    {
        _load.Load = 10;
        var (status, body) = await Get("/altruist/server/ready");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, status);
        Assert.Equal("Full", body.GetProperty("state").GetString());
        // Liveness is unaffected: the process is fine, it just takes nothing new.
        Assert.Equal(HttpStatusCode.OK, (await Get("/altruist/server/live")).Status);
    }

    [Fact]
    public async Task Posting_a_drain_from_loopback_starts_it_and_the_node_leaves_readiness()
    {
        using var res = await _http.PostAsync("/altruist/server/drain", null);
        Assert.Equal(HttpStatusCode.Accepted, res.StatusCode);
        Assert.Equal(1, _rooms.Begins);
        var (status, body) = await Get("/altruist/server/ready");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, status);
        Assert.Equal("Draining", body.GetProperty("state").GetString());
        // A second post joins the same drain.
        using var again = await _http.PostAsync("/altruist/server/drain", null);
        Assert.Equal(HttpStatusCode.Accepted, again.StatusCode);
        Assert.Equal(1, _rooms.Begins);
    }

    [Fact]
    public async Task A_waiting_drain_answers_once_everything_drained()
    {
        _ = Task.Delay(200).ContinueWith(_ => _rooms.Drained = true);
        var sw = Stopwatch.StartNew();
        using var res = await _http.PostAsync("/altruist/server/drain?wait=true", null);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.True(sw.ElapsedMilliseconds >= 150, $"{sw.ElapsedMilliseconds} ms");
        var body = JsonDocument.Parse(await res.Content.ReadAsStringAsync()).RootElement;
        Assert.True(body.GetProperty("drained").GetBoolean());
        Assert.Equal("Drained", body.GetProperty("state").GetString());
    }

    [Fact]
    public async Task A_waiting_drain_that_times_out_says_so()
    {
        _rooms.DrainsOnForce = true;
        using var res = await _http.PostAsync("/altruist/server/drain?wait=true&timeoutSeconds=0.1", null);
        Assert.Equal(HttpStatusCode.Accepted, res.StatusCode);
        var body = JsonDocument.Parse(await res.Content.ReadAsStringAsync()).RootElement;
        Assert.False(body.GetProperty("drained").GetBoolean());
        Assert.Equal(1, _rooms.Forces);
    }

    [Fact]
    public async Task A_negative_timeout_is_refused()
    {
        using var res = await _http.PostAsync("/altruist/server/drain?timeoutSeconds=-1", null);
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        Assert.False(_node.IsDraining);
    }

    [Fact]
    public async Task With_a_token_configured_only_the_token_may_drain()
    {
        _app.Services.GetRequiredService<IConfiguration>()["altruist:server:drain:token"] = "s3cret";

        using (var none = await _http.PostAsync("/altruist/server/drain", null))
            Assert.Equal(HttpStatusCode.Forbidden, none.StatusCode);
        using (var wrong = new HttpRequestMessage(HttpMethod.Post, "/altruist/server/drain"))
        {
            wrong.Headers.Add(ServerNodeController.TokenHeader, "guess");
            using var res = await _http.SendAsync(wrong);
            Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
        }
        Assert.False(_node.IsDraining);

        using var right = new HttpRequestMessage(HttpMethod.Post, "/altruist/server/drain");
        right.Headers.Add(ServerNodeController.TokenHeader, "s3cret");
        using var ok = await _http.SendAsync(right);
        Assert.Equal(HttpStatusCode.Accepted, ok.StatusCode);
        Assert.True(_node.IsDraining);
    }

    [Fact]
    public void The_host_waits_long_enough_for_a_drain()
    {
        var timeout = _app.Services.GetRequiredService<IOptions<HostOptions>>().Value.ShutdownTimeout;
        Assert.True(timeout >= TimeSpan.FromSeconds(21), $"shutdown timeout {timeout}");
    }

    [Fact]
    public async Task Stopping_the_host_drains_the_node_first_and_waits_for_it()
    {
        _ = Task.Delay(300).ContinueWith(_ => _rooms.Drained = true);
        var sw = Stopwatch.StartNew();
        await _app.StopAsync();
        _stopped = true;
        Assert.True(sw.ElapsedMilliseconds >= 250, $"stopped after {sw.ElapsedMilliseconds} ms, before the work drained");
        Assert.Equal(1, _rooms.Begins);
        Assert.Equal(0, _rooms.Forces);
        Assert.Equal(ServerNodeState.Drained, _node.State);
    }
}
