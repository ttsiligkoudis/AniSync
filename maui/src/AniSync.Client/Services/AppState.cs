using AniSync.Client.Models;

namespace AniSync.Client.Services;

/// <summary>
/// Session + chrome state that the layout reads to gate links and label the
/// media-type switch — the Blazor equivalent of the layoutLoggedIn /
/// layoutHasSession / layoutEnabled / layoutMediaType locals computed at the
/// top of _Layout.cshtml. Hydrated from the server (GET /api/v1/session) once
/// that endpoint exists; defaults below describe a connected AniList user so
/// the full chrome renders for now.
/// </summary>
public sealed class AppState
{
    public bool LoggedIn { get; private set; } = true;
    public bool HasSession { get; private set; } = true;
    public string ConnectedLabel { get; private set; } = "AniList";

    /// <summary>Modes the user enabled in the chooser (drives the switch + button icon).</summary>
    public IReadOnlyList<MetaType> EnabledMediaTypes { get; private set; } =
        new[] { MetaType.anime, MetaType.movie, MetaType.series };

    /// <summary>Currently active mode.</summary>
    public MetaType MediaType { get; private set; } = MetaType.anime;

    public string MediaTypeLabel => MediaType switch
    {
        MetaType.movie => "Movies",
        MetaType.series => "Series",
        _ => "Anime",
    };

    /// <summary>
    /// Dashboard-only client-side filter ("all" / "anime" / "movie" / "series").
    /// The dashboard media-type switch sets this without changing the global
    /// <see cref="MediaType"/>; Home shows/hides its sections to match.
    /// </summary>
    public string DashboardFilter { get; private set; } = "all";

    public event Action? Changed;

    public void SetMediaType(MetaType type)
    {
        if (MediaType == type) return;
        MediaType = type;
        Changed?.Invoke();
    }

    public void SetDashboardFilter(string filter)
    {
        if (DashboardFilter == filter) return;
        DashboardFilter = filter;
        Changed?.Invoke();
    }

    /// <summary>Apply session data fetched from the backend.</summary>
    public void Hydrate(bool loggedIn, bool hasSession, string connectedLabel,
        IReadOnlyList<MetaType> enabled, MetaType active)
    {
        LoggedIn = loggedIn;
        HasSession = hasSession;
        ConnectedLabel = connectedLabel;
        EnabledMediaTypes = enabled;
        MediaType = active;
        Changed?.Invoke();
    }
}
