using System.Net;
using System.Text.Json;
using Altruist.CLI.Runtime;

namespace Altruist.CLI.Tests;

public sealed class RuntimeChangesTests
{
    private sealed class RecordingHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public List<(HttpMethod Method, string Url, string? Body)> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add((request.Method, request.RequestUri!.ToString(),
                request.Content is null ? null : await request.Content.ReadAsStringAsync(ct)));
            return new HttpResponseMessage(status) { Content = new StringContent(body) };
        }
    }

    private sealed class FixedApproval(ApprovalOutcome outcome) : IChangeApproval
    {
        public List<string> Questions { get; } = [];

        public Task<ApprovalOutcome> AskAsync(string question, CancellationToken ct)
        {
            Questions.Add(question);
            return Task.FromResult(outcome);
        }
    }

    private static (RuntimeChanges Changes, RecordingHandler Handler) Changes(HttpStatusCode status = HttpStatusCode.NoContent, string body = "")
    {
        var handler = new RecordingHandler(status, body);
        var endpoint = DashboardEndpoint.FromEnvironment(_ => null);
        return (new RuntimeChanges(new DashboardClient(new HttpClient(handler), endpoint)), handler);
    }

    [Fact]
    public async Task sends_an_approved_change()
    {
        var (changes, handler) = Changes();

        var outcome = await changes.ApplyAsync(RuntimeChange.Disconnect("c-1"), new FixedApproval(ApprovalOutcome.Approved), default);

        Assert.Equal(new ChangeOutcome.Applied("Client c-1 is disconnected."), outcome);
        Assert.Equal((HttpMethod.Delete, "http://localhost:8080/dashboard/v1/sessions/connections/c-1", (string?)null), Assert.Single(handler.Requests));
    }

    [Fact]
    public async Task asks_before_sending_and_names_the_change()
    {
        var (changes, _) = Changes();
        var approval = new FixedApproval(ApprovalOutcome.Approved);

        await changes.ApplyAsync(RuntimeChange.CloseRoom("lobby-7"), approval, default);

        Assert.Contains("lobby-7", Assert.Single(approval.Questions));
    }

    [Theory]
    [InlineData(ApprovalOutcome.Declined, "declined")]
    [InlineData(ApprovalOutcome.ClientCannotAsk, "cannot ask")]
    public async Task sends_nothing_without_approval(ApprovalOutcome answer, string reason)
    {
        var (changes, handler) = Changes();

        var outcome = await changes.ApplyAsync(RuntimeChange.Drain(null), new FixedApproval(answer), default);

        Assert.Contains(reason, Assert.IsType<ChangeOutcome.NotApplied>(outcome).Message);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task reports_a_change_the_server_refused()
    {
        var (changes, _) = Changes(HttpStatusCode.BadRequest, "Config key 'altruist:x' is not live-editable.");

        var outcome = await changes.ApplyAsync(
            RuntimeChange.SetConfig(new Dictionary<string, string> { ["altruist:x"] = "1" }), new FixedApproval(ApprovalOutcome.Approved), default);

        Assert.Contains("not live-editable", Assert.IsType<ChangeOutcome.NotApplied>(outcome).Message);
    }

    [Fact]
    public void config_is_sent_as_the_batch_the_server_reads()
    {
        var change = RuntimeChange.SetConfig(new Dictionary<string, string> { ["altruist:a"] = "1", ["altruist:b"] = "two" });

        Assert.Equal(HttpMethod.Post, change.Request.Method);
        Assert.Equal("dashboard/v1/summary/config/update-batch", change.Request.RelativePath);
        Assert.Equal("[{\"key\":\"altruist:a\",\"value\":\"1\"},{\"key\":\"altruist:b\",\"value\":\"two\"}]", change.Request.JsonBody);
        Assert.Contains("altruist:b = two", change.Question);
    }

    [Fact]
    public void drain_passes_its_timeout()
    {
        Assert.Equal("dashboard/v1/server/drain?timeoutSeconds=2.5", RuntimeChange.Drain(2.5).Request.RelativePath);
        Assert.Equal("dashboard/v1/server/drain", RuntimeChange.Drain(null).Request.RelativePath);
    }

    [Fact]
    public void cache_entries_are_addressed_by_type_group_and_key()
    {
        var entry = new CacheEntryAddress("Game.Model.PlayerState, Game, Version=1.0.0.0", "eu", "p 1");

        var save = RuntimeChange.SaveCacheEntry(entry, JsonDocument.Parse("{\"hp\":10}").RootElement);
        var remove = RuntimeChange.RemoveCacheEntry(entry);

        Assert.Equal((HttpMethod.Put, "dashboard/v1/cache/entry"), (save.Request.Method, save.Request.RelativePath));
        Assert.Equal("{\"type\":\"Game.Model.PlayerState, Game, Version=1.0.0.0\",\"groupId\":\"eu\",\"key\":\"p 1\",\"value\":{\"hp\":10}}", save.Request.JsonBody);
        Assert.Equal("dashboard/v1/cache/entry?Type=Game.Model.PlayerState%2C%20Game%2C%20Version%3D1.0.0.0&GroupId=eu&Key=p%201", remove.Request.RelativePath);
        Assert.Contains("PlayerState eu/p 1", remove.Question);
    }
}
