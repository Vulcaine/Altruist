using System.Net;
using System.Text.Json;

namespace Altruist.CLI.Runtime;

/// <summary>Reads the dashboard API (<c>/dashboard/v1/...</c>) of a running Altruist server.</summary>
public sealed class DashboardClient(HttpClient http, DashboardEndpoint endpoint)
{
    // The header the server's DashboardAccessMiddleware reads the token from.
    private const string TokenHeader = "X-Altruist-Dashboard-Token";

    /// <summary>GETs a dashboard path such as <c>dashboard/v1/server</c> (relative, so the server's path base applies).</summary>
    public async Task<DashboardReply> GetAsync(string relativePath, CancellationToken ct)
    {
        if (endpoint.BaseUrl is null)
            return new DashboardReply.Failed(DashboardFailure.InvalidUrl,
                $"{DashboardEndpoint.UrlVariable} is \"{endpoint.ConfiguredUrl}\", which is not an http(s) URL. Set it to the server's address, for example {DashboardEndpoint.DefaultUrl}.");

        var url = new Uri(endpoint.BaseUrl, relativePath);
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (endpoint.Token is { } token)
            request.Headers.Add(TokenHeader, token);

        try
        {
            using var response = await http.SendAsync(request, ct);
            var body = await response.Content.ReadAsStringAsync(ct);
            return response.IsSuccessStatusCode ? new DashboardReply.Ok(body) : Refusal(url, response.StatusCode, body);
        }
        catch (HttpRequestException e)
        {
            return new DashboardReply.Failed(DashboardFailure.Unreachable,
                $"No Altruist server answers at {endpoint.BaseUrl} ({e.Message}). Start it (dotnet run), or set {DashboardEndpoint.UrlVariable} to its address.");
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            return new DashboardReply.Failed(DashboardFailure.Timeout,
                $"{url} did not answer within {http.Timeout.TotalSeconds:0} seconds.");
        }
    }

    private DashboardReply.Failed Refusal(Uri url, HttpStatusCode status, string body) =>
        (ErrorCode(body), status) switch
        {
            ("dashboard_disabled", _) => new(DashboardFailure.DashboardDisabled,
                "The server's dashboard is off. Set altruist:dashboard:enabled: true in its config.yml and restart it."),
            ("dashboard_unauthorized", _) => new(DashboardFailure.TokenRequired,
                $"The server asks for its dashboard token. Set {DashboardEndpoint.TokenVariable} to its altruist:dashboard:token."),
            ("dashboard_forbidden", _) => new(DashboardFailure.TokenRejected,
                $"The server rejected the token in {DashboardEndpoint.TokenVariable}: it is not its altruist:dashboard:token."),
            ("dashboard_loopback_only", _) => new(DashboardFailure.LoopbackOnly,
                $"Without altruist:dashboard:token the server only answers requests from its own machine. Run against localhost, or give the server a token and set {DashboardEndpoint.TokenVariable}."),
            ("dashboard_closed", _) => new(DashboardFailure.DashboardClosed,
                $"The server's dashboard answers no one: it is not in Development and has no altruist:dashboard:token or altruist:dashboard:policy. Give it a token and set {DashboardEndpoint.TokenVariable}."),
            (null, HttpStatusCode.NotFound) when string.IsNullOrWhiteSpace(body) => new(DashboardFailure.RouteNotMapped,
                $"The server maps no {url.AbsolutePath}. Its package is not referenced: the world, engine, gameplay, rooms and autosave views come with the gaming packages."),
            (_, HttpStatusCode.NotFound) => new(DashboardFailure.NotFound, $"Not found: {body}"),
            _ => new(DashboardFailure.ServerError, $"{url} answered {(int)status} {status}: {body}"),
        };

    // The refusals of DashboardAccessMiddleware are {"error": "<code>", "message": "..."}.
    private static string? ErrorCode(string body)
    {
        if (!body.TrimStart().StartsWith('{'))
            return null;
        try
        {
            using var json = JsonDocument.Parse(body);
            return json.RootElement.TryGetProperty("error", out var code) && code.ValueKind == JsonValueKind.String
                ? code.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
