namespace Altruist.CLI.Runtime;

/// <summary>Whether a change reached the running server.</summary>
public abstract record ChangeOutcome
{
    private ChangeOutcome() { }

    /// <summary>The person approved it and the server accepted it.</summary>
    /// <param name="Message">What changed, for the agent.</param>
    public sealed record Applied(string Message) : ChangeOutcome;

    /// <summary>Nothing changed.</summary>
    /// <param name="Message">Why, for the agent.</param>
    public sealed record NotApplied(string Message) : ChangeOutcome;
}

/// <summary>Makes changes to the running server, each only after the person at the client approved it.</summary>
public sealed class RuntimeChanges(DashboardClient dashboard)
{
    /// <summary>Asks for approval, then sends the change; nothing is sent unless the person approved it.</summary>
    public async Task<ChangeOutcome> ApplyAsync(RuntimeChange change, IChangeApproval approval, CancellationToken ct) =>
        await approval.AskAsync(change.Question, ct) switch
        {
            ApprovalOutcome.Approved => await SendAsync(change, ct),
            ApprovalOutcome.Declined => new ChangeOutcome.NotApplied(
                "The user declined the change; nothing was changed. Do not retry it unless they ask for it."),
            ApprovalOutcome.ClientCannotAsk => new ChangeOutcome.NotApplied(
                "This MCP client cannot ask the user to approve a change (it does not support MCP elicitation), so altruist "
                + "changes nothing from it. Tell the user what you wanted to change; they can do it in the dashboard."),
            var other => throw new InvalidOperationException($"Unknown approval outcome {other}."),
        };

    private async Task<ChangeOutcome> SendAsync(RuntimeChange change, CancellationToken ct) =>
        await dashboard.SendAsync(change.Request, ct) switch
        {
            DashboardReply.Ok ok => new ChangeOutcome.Applied(string.IsNullOrWhiteSpace(ok.Body) ? change.Done : $"{change.Done}\n{ok.Body}"),
            DashboardReply.Failed failed => new ChangeOutcome.NotApplied(failed.Message),
            var other => throw new InvalidOperationException($"Unknown dashboard reply {other.GetType().Name}."),
        };
}
