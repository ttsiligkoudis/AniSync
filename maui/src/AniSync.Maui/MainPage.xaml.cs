using Microsoft.AspNetCore.Components.WebView;

namespace AniSync;

public partial class MainPage : ContentPage
{
    public MainPage()
    {
        InitializeComponent();
        // Route external links (Sign up / Get API key / addon manifest pages, …) to the system browser
        // instead of loading them over the Blazor app. Android's WebView silently drops
        // <a target="_blank"> (no multi-window support), so those links did nothing in-app; native-links.js
        // rewrites them into a main-frame navigation, and this handler hands any external host off to the OS
        // browser (cancelling the in-WebView navigation, so the SPA stays put). Internal app navigations
        // (the BlazorWebView's own 0.0.0.0 origin) stay in the WebView.
        blazorWebView.UrlLoading += OnUrlLoading;
    }

    private static void OnUrlLoading(object? sender, UrlLoadingEventArgs e)
    {
        if (e.Url.Scheme is "http" or "https"
            && e.Url.Host is not ("0.0.0.0" or "0.0.0.1" or "localhost"))
            e.UrlLoadingStrategy = UrlLoadingStrategy.OpenExternally;
    }
}
