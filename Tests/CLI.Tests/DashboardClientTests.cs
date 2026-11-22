using System.Net;
using Altruist.CLI.Runtime;

namespace Altruist.CLI.Tests;

public sealed class DashboardClientTests
{
    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> answer) : HttpMessageHandler
    {
        public HttpRequestMessage? Last { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Last = request;
            return Task.FromResult(answer(request));
        }
    }

    private static DashboardEndpoint Endpoint(string? url = null, string? token = null) =>
        DashboardEndpoint.FromEnvironment(name => name switch
        {
            DashboardEndpoint.UrlVariable => url,
            DashboardEndpoint.TokenVariable => token,
            _ => null,
        });

    private static (DashboardClient Client, StubHandler Handler) Client(
        HttpStatusCode status, string body, DashboardEndpoint? endpoint = null)
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(status) { Content = new StringContent(body) });
        return (new DashboardClient(new HttpClient(handler), endpoint ?? Endpoint()), handler);
    }

    [Fact]
    public async Task returns_the_body_of_a_success()
    {
        var (client, _) = Client(HttpStatusCode.OK, "{\"draining\":false}");

        var reply = await client.GetAsync("dashboard/v1/server", default);

        Assert.Equal(new DashboardReply.Ok("{\"draining\":false}"), reply);
    }

    [Fact]
    public async Task asks_the_template_port_on_localhost_when_no_url_is_set()
    {
        var (client, handler) = Client(HttpStatusCode.OK, "{}");

        await client.GetAsync("dashboard/v1/server", default);

        Assert.Equal("http://localhost:8080/dashboard/v1/server", handler.Last!.RequestUri!.ToString());
    }

    [Fact]
    public async Task keeps_the_path_base_of_the_url()
    {
        var (client, handler) = Client(HttpStatusCode.OK, "{}", Endpoint("http://game.local:9000/api"));

        await client.GetAsync("dashboard/v1/server", default);

        Assert.Equal("http://game.local:9000/api/dashboard/v1/server", handler.Last!.RequestUri!.ToString());
    }

    [Fact]
    public async Task sends_the_token_in_the_dashboard_header()
    {
        var (client, handler) = Client(HttpStatusCode.OK, "{}", Endpoint(token: "0123456789abcdef"));

        await client.GetAsync("dashboard/v1/server", default);

        Assert.Equal(["0123456789abcdef"], handler.Last!.Headers.GetValues("X-Altruist-Dashboard-Token"));
    }

    [Fact]
    public async Task reports_a_url_that_is_not_http_without_sending()
    {
        var (client, handler) = Client(HttpStatusCode.OK, "{}", Endpoint("localhost:8080"));

        var reply = await client.GetAsync("dashboard/v1/server", default);

        Assert.Equal(DashboardFailure.InvalidUrl, Assert.IsType<DashboardReply.Failed>(reply).Failure);
        Assert.Null(handler.Last);
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound, "dashboard_disabled", DashboardFailure.DashboardDisabled)]
    [InlineData(HttpStatusCode.Unauthorized, "dashboard_unauthorized", DashboardFailure.TokenRequired)]
    [InlineData(HttpStatusCode.Forbidden, "dashboard_forbidden", DashboardFailure.TokenRejected)]
    [InlineData(HttpStatusCode.Forbidden, "dashboard_loopback_only", DashboardFailure.LoopbackOnly)]
    [InlineData(HttpStatusCode.ServiceUnavailable, "dashboard_closed", DashboardFailure.DashboardClosed)]
    public async Task names_why_the_dashboard_refused(HttpStatusCode status, string code, DashboardFailure expected)
    {
        var (client, _) = Client(status, $"{{\"error\":\"{code}\",\"message\":\"refused\"}}");

        var reply = await client.GetAsync("dashboard/v1/server", default);

        Assert.Equal(expected, Assert.IsType<DashboardReply.Failed>(reply).Failure);
    }

    [Fact]
    public async Task an_empty_404_is_a_route_the_server_does_not_map()
    {
        var (client, _) = Client(HttpStatusCode.NotFound, "");

        var reply = await client.GetAsync("dashboard/v1/worlds", default);

        Assert.Equal(DashboardFailure.RouteNotMapped, Assert.IsType<DashboardReply.Failed>(reply).Failure);
    }

    [Fact]
    public async Task a_404_with_a_message_is_a_missing_item()
    {
        var (client, _) = Client(HttpStatusCode.NotFound, "{\"message\":\"World 7 not found.\"}");

        var reply = Assert.IsType<DashboardReply.Failed>(await client.GetAsync("dashboard/v1/worlds/7/objects", default));

        Assert.Equal(DashboardFailure.NotFound, reply.Failure);
        Assert.Contains("World 7 not found.", reply.Message);
    }

    [Fact]
    public async Task a_refused_connection_means_no_server_runs_there()
    {
        var handler = new StubHandler(_ => throw new HttpRequestException("Connection refused"));
        var client = new DashboardClient(new HttpClient(handler), Endpoint());

        var reply = await client.GetAsync("dashboard/v1/server", default);

        Assert.Equal(DashboardFailure.Unreachable, Assert.IsType<DashboardReply.Failed>(reply).Failure);
    }
}
