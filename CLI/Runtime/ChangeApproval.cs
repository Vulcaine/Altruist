namespace Altruist.CLI.Runtime;

/// <summary>What the person said when asked to approve a change to the running server.</summary>
public enum ApprovalOutcome
{
    /// <summary>They approved it.</summary>
    Approved,
    /// <summary>They declined or dismissed the question.</summary>
    Declined,
    /// <summary>The client has no way to ask them, so nobody approved it.</summary>
    ClientCannotAsk,
}

/// <summary>Asks the person at the client to approve one change.</summary>
public interface IChangeApproval
{
    /// <summary>Shows the question and returns their answer.</summary>
    Task<ApprovalOutcome> AskAsync(string question, CancellationToken ct);
}
