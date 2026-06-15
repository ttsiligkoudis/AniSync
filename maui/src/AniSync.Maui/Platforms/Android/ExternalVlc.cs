using Android.Content;
using AniSync.Client.Services;

namespace AniSync;

// Hands a stream off to the standalone VLC for Android app via an ACTION_VIEW intent — the same path
// Stremio uses when "VLC" is picked as the external player. Background: on-screen diagnostics showed the
// embedded LibVLCSharp VideoView can't bring up a video output for 4K on budget Android TV GPUs (the vout
// never initialises — "vout x0" — so the decoder fills its picture pool and stalls after a few frames),
// yet the SAME 4K file plays fine in standalone VLC, which hardware-decodes it zero-copy onto its own
// SurfaceView in a dedicated Activity. So on TV we delegate to VLC; phones keep the in-app player (it works
// there and carries the custom chrome / subtitle picker / resume + scrobble). Returns false — caller falls
// back to the embedded player — when VLC isn't installed or the intent can't be resolved.
internal static class ExternalVlc
{
    // VLC for Android / Android TV share this application id.
    private const string Package = "org.videolan.vlc";

    public static bool TryLaunch(PlaybackRequest request)
    {
        try
        {
            var ctx = (Context?)Platform.CurrentActivity ?? Android.App.Application.Context;
            if (ctx?.PackageManager is not { } pm) return false;

            using var intent = new Intent(Intent.ActionView);
            intent.SetPackage(Package);
            intent.SetDataAndType(Android.Net.Uri.Parse(request.Url), "video/*");

            // Extras understood by VLC's VideoPlayerActivity.
            intent.PutExtra("title", request.Title);
            if (request.ResumeSeconds is > 0)
            {
                intent.PutExtra("from_start", false);
                intent.PutExtra("position", (long)(request.ResumeSeconds.Value * 1000)); // milliseconds
            }
            else
            {
                intent.PutExtra("from_start", true);
            }
            // Best-effort: VLC loads a single "subtitles_location". Ours are remote proxied URLs and VLC may
            // only reliably honour a local path, so it can ignore this — not fatal, just no external sub.
            if (request.Subtitles is { Count: > 0 } subs)
                intent.PutExtra("subtitles_location", subs[0].Url);

            // Launching from the application context (no foreground Activity) needs its own task.
            if (Platform.CurrentActivity is null)
                intent.AddFlags(ActivityFlags.NewTask);

            // VLC not installed / can't handle the link → let the caller use the embedded player instead.
            if (intent.ResolveActivity(pm) is null) return false;

            ctx.StartActivity(intent);
            return true;
        }
        catch
        {
            // Any failure (resolve, security, malformed url) → fall back to the embedded player.
            return false;
        }
    }
}
