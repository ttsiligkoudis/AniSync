using Foundation;
using UIKit;

namespace AniSync;

[Register("AppDelegate")]
public class AppDelegate : MauiUIApplicationDelegate
{
    protected override MauiApp CreateMauiApp() => MauiProgram.CreateMauiApp();

    // Forward the anisync:// native-auth callback to MAUI's WebAuthenticator
    // (the anisync scheme is declared in Info.plist's CFBundleURLTypes).
    public override bool OpenUrl(UIApplication app, NSUrl url, NSDictionary options)
        => Microsoft.Maui.Authentication.WebAuthenticator.Default.OpenUrl(new Uri(url.AbsoluteString))
           || base.OpenUrl(app, url, options);
}
