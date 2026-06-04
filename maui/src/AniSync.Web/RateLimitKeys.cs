namespace AniSync.Web;

/// <summary>
/// Partition-key helper for the per-client rate limits. Prefers Fly's
/// <c>Fly-Client-IP</c> header (set/overwritten by Fly's edge, so it can't be
/// spoofed), falling back to the connection remote IP and finally a constant.
/// Carried over from the ASP.NET app's Program.cs.
/// </summary>
internal static class RateLimitKeys
{
    public static string ClientIp(HttpContext ctx)
    {
        var fly = ctx.Request.Headers["Fly-Client-IP"].ToString();
        if (!string.IsNullOrWhiteSpace(fly)) return fly.Trim();
        return ctx.Connection.RemoteIpAddress?.ToString() ?? "anonymous";
    }
}
