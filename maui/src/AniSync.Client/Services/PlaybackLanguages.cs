namespace AniSync.Client.Services;

/// <summary>
/// The language options offered for the default-audio / default-subtitle settings, plus the
/// matching helpers both heads use to pick a track. Codes are ISO 639-1. English is the default
/// when nothing is stored.
/// </summary>
public static class PlaybackLanguages
{
    public const string Default = "en";

    /// <summary>(code, display name) — English first; the rest alphabetical-ish by name.</summary>
    public static readonly (string Code, string Name)[] Options =
    {
        ("en", "English"),
        ("ar", "Arabic"),
        ("zh", "Chinese"),
        ("nl", "Dutch"),
        ("fr", "French"),
        ("de", "German"),
        ("hi", "Hindi"),
        ("id", "Indonesian"),
        ("it", "Italian"),
        ("ja", "Japanese"),
        ("ko", "Korean"),
        ("pl", "Polish"),
        ("pt", "Portuguese"),
        ("ru", "Russian"),
        ("es", "Spanish"),
        ("sv", "Swedish"),
        ("th", "Thai"),
        ("tr", "Turkish"),
        ("vi", "Vietnamese"),
    };

    /// <summary>Normalise to a lowercase 2-letter code; blank → English default.</summary>
    public static string Normalize(string? code)
        => string.IsNullOrWhiteSpace(code) ? Default : code.Trim().ToLowerInvariant();

    public static bool IsEnglish(string? code) => Normalize(code) == "en";

    /// <summary>Display name for a code (falls back to the upper-cased code).</summary>
    public static string DisplayName(string? code)
    {
        var c = Normalize(code);
        foreach (var (Code, Name) in Options)
            if (Code == c) return Name;
        return c.ToUpperInvariant();
    }
}
