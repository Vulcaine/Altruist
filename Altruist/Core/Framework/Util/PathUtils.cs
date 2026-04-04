namespace Altruist;

public static class PathUtils
{
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
