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

    /// <summary>True once the session/auth state has been resolved from storage at startup
    /// (interactive). The chrome + dashboard wait for this so a signed-in user never sees the
    /// signed-out chrome/content flash first — everything renders neutral until the real state
    /// is known, then commits once. Stays false through prerender (no localStorage there).</summary>
    public bool SessionHydrated { get; private set; }

    /// <summary>True once MainLayout has finished reading the stored config credential from
    /// localStorage on the interactive pass (whether or not one was found). Distinct from
    /// <see cref="SessionHydrated"/>, which the cookie-backed prerender bridge can flip true
    /// EARLY (before the credential is read) just to render signed-in chrome. The dashboard
    /// gates its data-bearing content on THIS so its shelves/stats/layout fetch only after the
    /// X-AniSync-Config credential is available — otherwise they'd fire credential-less and come
    /// back empty (the "refresh shows me logged out" bug).</summary>
    public bool ConfigHydrated { get; private set; }

    /// <summary>Mark the config credential resolved (called by MainLayout after its localStorage
    /// hydration completes). Idempotent.</summary>
    public void MarkConfigHydrated()
    {
        if (ConfigHydrated) return;
        ConfigHydrated = true;
        Changed?.Invoke();
    }

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

    /// <summary>True once the saved dashboard layout has been resolved for this session
    /// (loaded from the account, or determined to be the default for an anonymous/no-config
    /// visitor). The dashboard waits for this before rendering shelves so they paint in the
    /// saved order/visibility once, instead of flashing the default order then reordering.
    /// Survives client-side navigation (AppState is circuit-scoped), so revisiting Home
    /// doesn't re-gate or re-fetch.</summary>
    public bool DashLayoutLoaded { get; private set; }

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

    /// <summary>
    /// The locale used to FORMAT dates/times for the user (day/month order, month names).
    /// Resolved from the browser/WebView's navigator.language (per circuit on the web head —
    /// CurrentCulture there is the shared server's, not the visitor's). Defaults to the
    /// process culture, which is correct on native (device locale) until the JS read lands.
    /// </summary>
    public System.Globalization.CultureInfo UiCulture { get; private set; } = System.Globalization.CultureInfo.CurrentCulture;

    public void SetUiCulture(System.Globalization.CultureInfo culture)
    {
        if (Equals(UiCulture, culture)) return;
        UiCulture = culture;
        Changed?.Invoke();
    }

    /// <summary>
    /// Web-only TV-preview override. The native head decides TV via DeviceIdiom; on the web
    /// <see cref="IAppEnvironment.IsTv"/> reads this so a browser can preview the full 10-foot
    /// shell with <c>?tv=1</c> (and <c>?tv=0</c> to exit). Never set by default — strictly opt-in.
    /// </summary>
    public bool ForceTv { get; private set; }

    public void SetForceTv(bool on)
    {
        if (ForceTv == on) return;
        ForceTv = on;
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
        DashLayoutLoaded = true;
        Changed?.Invoke();
    }

    /// <summary>Mark the layout resolved without changing it — for the anonymous / no-config
    /// case where there's nothing saved to load and the default order stands.</summary>
    public void MarkDashLayoutResolved()
    {
        if (DashLayoutLoaded) return;
        DashLayoutLoaded = true;
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
        SessionHydrated = true;
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
