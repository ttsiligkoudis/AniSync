using Android.App;
using AniSync.Client.Services;
using Microsoft.Maui.ApplicationModel;

namespace AniSync;

// Android IPlatformChrome: re-tints the OS system bars when the web app's theme bridge reports a change.
// Must touch the window on the UI thread.
public sealed class AndroidPlatformChrome : IPlatformChrome
{
    public void SetTheme(bool dark)
    {
        MainThread.BeginInvokeOnMainThread(() =>
        {
            if (Platform.CurrentActivity is Activity activity)
                AndroidSystemBars.Apply(activity, dark);
        });
    }
}
