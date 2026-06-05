using System.Text.Json;
using System.Text.Json.Serialization;

namespace AniSync.Client.Services;

/// <summary>One dashboard section in the user's saved layout (order = list position).</summary>
public sealed class LayoutEntry
{
    [JsonPropertyName("key")] public string Key { get; set; } = "";
    [JsonPropertyName("visible")] public bool Visible { get; set; } = true;
}

/// <summary>
/// The dashboard's customisable sections + parse/merge/serialize — a C# port of
/// wwwroot/js/dashboard-layout.js (UNITS / DEFAULT_ORDER / HIDDEN_BY_DEFAULT / load()). The
/// reorder + hide is applied in Blazor (CSS flexbox <c>order</c> + <c>hidden</c>), NOT by
/// DOM-rewriting JS, so it doesn't fight the renderer. Persisted to the account via
/// /api/v1/me/dashboard-layout; the layout JSON shape (<c>[{key,visible}]</c>) matches the web's
/// so a config carries over between the two apps.
/// </summary>
public static class DashboardLayout
{
    /// <summary>Every customisable section in DEFAULT order, with the modal label. Keys match the
    /// [data-dash-section] / DashboardShelf.DashSection values the dashboard stamps.</summary>
    public static readonly (string Key, string Label)[] Units =
    {
        ("stats",                    "Your stats"),
        ("anime-new-episodes",       "New Episodes Today"),
        ("anime-continue",           "Continue watching · Anime"),
        ("video-continue-movie",     "Continue watching · Movies"),
        ("video-continue-series",    "Continue watching · Series"),
        ("anime-trending",           "Trending · Anime"),
        ("video-trending-movie",     "Trending · Movies"),
        ("video-trending-series",    "Trending · Series"),
        ("anime-popular",            "Most Popular · Anime"),
        ("video-popular-movie",      "Most Popular · Movies"),
        ("video-popular-series",     "Most Popular · Series"),
        ("anime-anticipated",        "Most Anticipated · Anime"),
        ("video-anticipated-movie",  "Most Anticipated · Movies"),
        ("video-anticipated-series", "Most Anticipated · Series"),
        ("browse",                   "Browse By"),
    };

    // New Episodes Today is opt-in (hidden until the user enables it), matching the web.
    private static readonly HashSet<string> HiddenByDefault = new(StringComparer.Ordinal) { "anime-new-episodes" };

    public static string LabelFor(string key) => Units.FirstOrDefault(u => u.Key == key).Label ?? key;

    public static bool DefaultVisible(string key) => !HiddenByDefault.Contains(key);

    /// <summary>The default layout (server render order; opt-in shelves hidden).</summary>
    public static List<LayoutEntry> Default() =>
        Units.Select(u => new LayoutEntry { Key = u.Key, Visible = DefaultVisible(u.Key) }).ToList();

    /// <summary>
    /// Parse the saved layout JSON and merge with the defaults: keep the saved order + visibility
    /// for known keys, drop unknown/retired keys, and append any new default key the saved layout
    /// never knew about (at its default visibility). Mirrors dashboard-layout.js load().
    /// </summary>
    public static List<LayoutEntry> Merge(string? savedJson)
    {
        var known = new HashSet<string>(Units.Select(u => u.Key), StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<LayoutEntry>();

        if (!string.IsNullOrWhiteSpace(savedJson))
        {
            List<LayoutEntry>? saved = null;
            try { saved = JsonSerializer.Deserialize<List<LayoutEntry>>(savedJson); } catch { /* corrupt → defaults */ }
            if (saved is not null)
            {
                foreach (var e in saved)
                {
                    if (e is null || string.IsNullOrEmpty(e.Key)) continue;
                    if (known.Contains(e.Key) && seen.Add(e.Key))
                        result.Add(new LayoutEntry { Key = e.Key, Visible = e.Visible });
                }
            }
        }
        // Append any default key the saved layout didn't mention, at its default visibility.
        foreach (var u in Units)
            if (seen.Add(u.Key)) result.Add(new LayoutEntry { Key = u.Key, Visible = DefaultVisible(u.Key) });
        return result;
    }

    public static string ToJson(IEnumerable<LayoutEntry> layout) =>
        JsonSerializer.Serialize(layout.Select(e => new LayoutEntry { Key = e.Key, Visible = e.Visible }).ToList());
}
