namespace Altruist;

/// <summary>Route/path string helpers shared by the transports and portal routing.</summary>
public static class PathUtils
{
    /// <summary>
    /// Joins route segments into one canonical path with exactly one leading and one trailing slash,
    /// e.g. <c>NormalizeRoute("api", "/v1/", "game")</c> returns <c>"/api/v1/game/"</c>.
    /// </summary>
    /// <remarks>
    /// Null/blank segments are skipped; each segment is trimmed of whitespace and of leading/trailing <c>/</c>
    /// (inner slashes are kept, duplicated inner slashes are not collapsed). Case is preserved. No segments
    /// yields <c>"/"</c>. Use it to compare routes regardless of slash style: portal routes
    /// (<see cref="IPortal.Route"/>) and incoming WebSocket request paths are both normalized with it.
    /// </remarks>
    /// <param name="segments">Route segments in order.</param>
    /// <returns>The normalized route, always starting and ending with <c>/</c>.</returns>
    public static string NormalizeRoute(params string?[] segments)
    {
        var parts = new List<string>(segments.Length);

        foreach (var seg in segments)
        {
            if (string.IsNullOrWhiteSpace(seg))
                continue;

            var trimmed = seg.Trim().Trim('/');
            if (!string.IsNullOrEmpty(trimmed))
                parts.Add(trimmed);
        }

        if (parts.Count == 0)
        {
            return "/";
        }

        return "/" + string.Join('/', parts) + "/";
    }
}
