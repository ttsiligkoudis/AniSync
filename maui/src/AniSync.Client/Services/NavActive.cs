namespace AniSync.Client.Services;

/// <summary>
/// Maps the current relative path to the layout's "activeNav" token so the
/// drawer / header / bottom-nav highlight the same section the MVC layout did
/// (e.g. /configure and /stremio both light up "configure").
/// </summary>
public static class NavActive
{
    public static string From(string relativePath)
    {
        var p = "/" + relativePath.Split('?', '#')[0].Trim('/');
        if (p == "/") return "home";
        if (p.StartsWith("/search")) return "search";
        if (p.StartsWith("/library")) return "library";
        if (p.StartsWith("/discover") || p.StartsWith("/meta") || p.StartsWith("/watch")) return "discover";
        if (p.StartsWith("/calendar")) return "calendar";
        if (p.StartsWith("/notifications")) return "notifications";
        if (p.StartsWith("/settings") || p.StartsWith("/configure") || p.StartsWith("/stremio")
            || p.StartsWith("/account") || p.StartsWith("/advanced")) return "configure";
        return "";
    }
}
