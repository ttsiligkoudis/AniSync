using AniSync.Client.Services;

namespace AniSync.Maui;

/// <summary>
/// MAUI head's <see cref="IAppEnvironment"/>. Drop into the generated
/// AniSync.Maui project (it references AniSync.Client). Points the thin client
/// at the deployed backend and declares native LibVLC playback support.
/// </summary>
public sealed class MauiAppEnvironment : IAppEnvironment
{
    public string ApiBaseUrl { get; }
    public bool IsNative => true;
    public bool SupportsNativePlayback => true;

    // Resolved once at startup from the device idiom (matches VlcPlayerPage's TV check). Drives the shared
    // Blazor UI's TV shell. Phones/tablets report a non-TV idiom, so they keep the normal chrome.
    public bool IsTv { get; } = DeviceInfo.Current.Idiom == DeviceIdiom.TV;

    public MauiAppEnvironment(string apiBaseUrl) => ApiBaseUrl = apiBaseUrl;
}
