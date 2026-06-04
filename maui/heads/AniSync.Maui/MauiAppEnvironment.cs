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

    public MauiAppEnvironment(string apiBaseUrl) => ApiBaseUrl = apiBaseUrl;
}
