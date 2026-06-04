using Android.App;
using Android.Content;
using Android.Content.PM;

namespace AniSync;

/// <summary>
/// Receives the <c>anisync://auth</c> redirect that ends a native OAuth login and
/// hands it to MAUI's <c>WebAuthenticator</c> (see <c>MauiNativeAuth</c>). The
/// intent-filter must match the callback URI registered in the auth flow:
/// scheme <c>anisync</c>, host <c>auth</c>.
/// </summary>
[Activity(NoHistory = true, LaunchMode = LaunchMode.SingleTop, Exported = true)]
[IntentFilter(
    new[] { Intent.ActionView },
    Categories = new[] { Intent.CategoryDefault, Intent.CategoryBrowsable },
    DataScheme = "anisync",
    DataHost = "auth")]
public class WebAuthenticatorCallbackActivity : Microsoft.Maui.Authentication.WebAuthenticatorCallbackActivity
{
}
