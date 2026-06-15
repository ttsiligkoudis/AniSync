using Android.Views;

namespace AniSync;

// Detects when the native video SurfaceView inside the LibVLCSharp VideoView actually has its Surface
// created, so the player only calls Play() once MediaCodec has a real output surface to render into.
// Starting hardware decode before the surface exists is what left the TV decoder with no output (a few
// frames decoded, then a permanent black-video stall) — standalone VLC avoids it by waiting for
// surfaceCreated. The caller keeps a timed safety net, so returning false (no SurfaceView found) is fine.
internal static class AndroidVideoSurface
{
    // Invoke onReady once the surface under 'root' is created (or immediately if it already is). Returns
    // false when no SurfaceView is found (caller falls back to its own timer).
    public static bool WhenReady(global::Android.Views.View? root, Action onReady)
    {
        if (FindSurfaceView(root) is not { } surface || surface.Holder is not { } holder)
            return false;

        // Already created → go now (libVLCSharp's own surfaceCreated callback, added when MediaPlayer was
        // set, has already run and attached the surface to the player).
        if (holder.Surface?.IsValid == true)
        {
            onReady();
            return true;
        }

        // Otherwise wait for it. We register AFTER libVLCSharp did, and Android invokes holder callbacks in
        // registration order, so the surface is attached to the player before our onReady (Play) runs.
        holder.AddCallback(new SurfaceReadyCallback(onReady, holder));
        return true;
    }

    private static SurfaceView? FindSurfaceView(global::Android.Views.View? v)
    {
        if (v is SurfaceView sv) return sv;
        if (v is ViewGroup g)
            for (int i = 0; i < g.ChildCount; i++)
                if (FindSurfaceView(g.GetChildAt(i)) is { } found)
                    return found;
        return null;
    }

    private sealed class SurfaceReadyCallback : Java.Lang.Object, ISurfaceHolderCallback
    {
        private Action? _onReady;            // nulled after firing so we only start once
        private readonly ISurfaceHolder _holder;

        public SurfaceReadyCallback(Action onReady, ISurfaceHolder holder)
        {
            _onReady = onReady;
            _holder = holder;
        }

        public void SurfaceCreated(ISurfaceHolder holder)
        {
            var cb = _onReady;
            _onReady = null;
            try { _holder.RemoveCallback(this); } catch { /* already gone */ }
            cb?.Invoke();
        }

        public void SurfaceChanged(ISurfaceHolder holder, Android.Graphics.Format format, int width, int height) { }
        public void SurfaceDestroyed(ISurfaceHolder holder) { }
    }
}
