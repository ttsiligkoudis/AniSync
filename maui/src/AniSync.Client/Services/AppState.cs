using AniSync.Client.Models;

namespace AniSync.Client.Services;

/// <summary>
/// Session + chrome state that the layout reads to gate links and label the
/// media-type switch — the Blazor equivalent of the layoutLoggedIn /
/// layoutHasSession / layoutEnabled / layoutMediaType locals computed at the
/// top of _Layout.cshtml. Hydrated at startup by MainLayout: "logged in" means a
/// config credential is stored (<see cref="StreamConfig"/>), and the connected-
/// services label is resolved from GET /api/v1/me/linked. Defaults are logged-out
/// so anonymous users correctly see the signed-out chrome.
/// </summary>
public sealed class AppState
{
    public bool LoggedIn { get; private set; }
    public bool HasSession { get; private set; }
    public string ConnectedLabel { get; private set; } = "";

    /// <summary>True once the media-type preference has been read from storage at
    /// startup (interactive). The dashboard + media-type switch wait for this so they
    /// render the chosen modes directly instead of flashing the default (all) first.</summary>
    public bool MediaTypesHydrated { get; private set; }

    public void MarkMediaTypesHydrated()
    {
        if (MediaTypesHydrated) return;
        MediaTypesHydrated = true;
        Changed?.Invoke();
    }

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

    /// <summary>The dashboard section layout (order + visibility). Home loads it from
    /// /api/v1/me/dashboard-layout; the Customize modal edits + persists it. StatsStrip /
    /// DashboardShelf / the Browse section read their order + hidden state from here and
    /// re-render on Changed. Reorder is applied via CSS flex `order`, not DOM-moving JS.</summary>
    public IReadOnlyList<LayoutEntry> DashLayout { get; private set; } = DashboardLayout.Default();

    /// <summary>
    /// The user's Stremio addon config string, used to resolve playable sources
    /// (GET /{config}/stream/...). Held in secure storage on MAUI and supplied
    /// at sign-in; null/empty means streaming isn't set up yet, so the watch
    /// page shows a "set up streaming" prompt.
    /// </summary>
    public string? StreamConfig { get; private set; }

    public void SetStreamConfig(string? config)
    {
        StreamConfig = config;
        Changed?.Invoke();
    }

    public event Action? Changed;

    /// <summary>Raised when the header's mode button asks to reopen the chooser.</summary>
    public event Action? MediaTypeModalRequested;

    /// <summary>Ask the rendered <c>MediaTypeModal</c> to open (the [data-media-type-open] reopen).</summary>
    public void RequestMediaTypeModal() => MediaTypeModalRequested?.Invoke();

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

    public void SetDashLayout(IReadOnlyList<LayoutEntry> layout)
    {
        DashLayout = layout;
        Changed?.Invoke();
    }

    /// <summary>
    /// Apply session state resolved at startup. <paramref name="loggedIn"/> is
    /// whether a config credential is stored; <paramref name="connectedLabel"/>
    /// is the resolved "Primary · Linked" services string (empty when it can't
    /// be resolved, in which case the chrome shows no "Connected to X" line).
    /// </summary>
    public void HydrateSession(bool loggedIn, string connectedLabel)
    {
        LoggedIn = loggedIn;
        HasSession = loggedIn;
        ConnectedLabel = connectedLabel ?? "";
        Changed?.Invoke();
    }

    /// <summary>Apply the user's enabled/active media types (from persistence).</summary>
    public void SetEnabledMediaTypes(IReadOnlyList<MetaType> enabled, MetaType active)
    {
        EnabledMediaTypes = enabled;
        MediaType = active;
        MediaTypesHydrated = true;
        Changed?.Invoke();
    }
}
