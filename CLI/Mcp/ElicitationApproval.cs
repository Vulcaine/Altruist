using Altruist.CLI.Runtime;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Altruist.CLI.Mcp;

/// <summary>Asks for approval through MCP elicitation: the client shows the question to the person and returns their answer.</summary>
public sealed class ElicitationApproval(McpServer server) : IChangeApproval
{
    public async Task<ApprovalOutcome> AskAsync(string question, CancellationToken ct)
    {
        if (server.ClientCapabilities?.Elicitation is null)
            return ApprovalOutcome.ClientCannotAsk;

        // No fields: the question itself is the form, and accepting it is the approval.
        var answer = await server.ElicitAsync(new ElicitRequestParams
        {
            Message = question,
            RequestedSchema = new ElicitRequestParams.RequestSchema(),
        }, ct);
        return answer.IsAccepted ? ApprovalOutcome.Approved : ApprovalOutcome.Declined;
    }
}
