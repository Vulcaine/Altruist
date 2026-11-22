namespace Altruist.CLI.Runtime;

/// <summary>Why a dashboard request gave no data.</summary>
public enum DashboardFailure
{
    /// <summary><see cref="DashboardEndpoint.UrlVariable"/> is not an http(s) URL.</summary>
    InvalidUrl,
    /// <summary>Nothing accepted the connection: the server is not running at that address.</summary>
    Unreachable,
    /// <summary>The server did not answer in time.</summary>
    Timeout,
    /// <summary><c>altruist:dashboard:enabled</c> is not true on the server.</summary>
    DashboardDisabled,
    /// <summary>The server needs a token and none was sent.</summary>
    TokenRequired,
    /// <summary>The token sent is not the server's.</summary>
    TokenRejected,
    /// <summary>Without a token the server only answers loopback requests.</summary>
    LoopbackOnly,
    /// <summary>The server has neither a token nor a policy and is not in Development, so the dashboard answers no one.</summary>
    DashboardClosed,
    /// <summary>The server maps no such route: the package that provides this view is not referenced.</summary>
    RouteNotMapped,
    /// <summary>The route exists, the item asked for does not.</summary>
    NotFound,
    /// <summary>Any other non-success answer.</summary>
    ServerError,
}

/// <summary>The outcome of one dashboard request.</summary>
public abstract record DashboardReply
{
    private DashboardReply() { }

    /// <summary>A success answer.</summary>
    /// <param name="Body">The response body (JSON).</param>
    public sealed record Ok(string Body) : DashboardReply;

    /// <summary>No data.</summary>
    /// <param name="Failure">Why.</param>
    /// <param name="Message">What happened and what to change, for the agent.</param>
    public sealed record Failed(DashboardFailure Failure, string Message) : DashboardReply;
}
