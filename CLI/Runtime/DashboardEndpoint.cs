namespace Altruist.CLI.Runtime;

/// <summary>
/// Where the running Altruist server's dashboard API is, and the token it asks for, read from the environment the
/// MCP client starts <c>altruist mcp</c> with.
/// </summary>
/// <param name="BaseUrl">The server's HTTP address, including its <c>altruist:server:http:path</c> base; absent when the configured value is not an http(s) URL.</param>
/// <param name="Token">The server's <c>altruist:dashboard:token</c>, when one is set.</param>
/// <param name="ConfiguredUrl">The URL as configured, for error messages.</param>
public sealed record DashboardEndpoint(Uri? BaseUrl, string? Token, string ConfiguredUrl)
{
    public const string UrlVariable = "ALTRUIST_DASHBOARD_URL";
    public const string TokenVariable = "ALTRUIST_DASHBOARD_TOKEN";
    // The port a project created from the template listens on.
    public const string DefaultUrl = "http://localhost:8080";

    public static DashboardEndpoint FromEnvironment(Func<string, string?> readVariable)
    {
        var configured = NonEmpty(readVariable(UrlVariable)) ?? DefaultUrl;
        // A trailing slash makes the base a directory, so relative dashboard paths land under the server's path base.
        var withSlash = configured.EndsWith('/') ? configured : configured + "/";
        var baseUrl = Uri.TryCreate(withSlash, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" ? uri : null;
        return new DashboardEndpoint(baseUrl, NonEmpty(readVariable(TokenVariable)), configured);
    }

    private static string? NonEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
