using LibVLCSharp.Shared;
using LibVLCSharp.MAUI;

namespace AniSync.Maui;

/// <summary>
/// Full-screen native player page hosting a LibVLCSharp <see cref="MediaElement"/>
/// (VideoView) bound to the MediaPlayer that <see cref="VlcMediaPlayer"/> built.
/// Pushed modally over the BlazorWebView shell so the native video surface
/// renders outside the WebView DOM. Closing the page stops playback.
/// </summary>
public sealed class VlcPlayerPage : ContentPage
{
    private readonly MediaPlayer _player;

    public VlcPlayerPage(MediaPlayer player, string title)
    {
        _player = player;
        Title = title;
        BackgroundColor = Colors.Black;

        var video = new MediaElement { MediaPlayer = player, VerticalOptions = LayoutOptions.Fill, HorizontalOptions = LayoutOptions.Fill };
        Content = new Grid { Children = { video } };

        // Stop + release when the user backs out of the player.
        NavigatedFrom += (_, _) =>
        {
            try { _player.Stop(); } catch { /* already stopped */ }
        };
    }
}
