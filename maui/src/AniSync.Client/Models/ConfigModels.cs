namespace AniSync.Client.Models;

// DTOs + helpers for the configure / account / advanced surface (header-authed
// /api/v1/me/config, stream-addons, scrobble, export/import, danger zone).
// PascalCase binds to the API's camelCase (Web JSON defaults).

public sealed class StreamAddonDto
{
    public string? Url { get; set; }
    public string? Name { get; set; }
}

/// <summary>Full configure-page state — GET /api/v1/me/config.</summary>
public sealed class ConfigStateDto
{
    /// <summary>v5 segment + revision — the `{config}` for the Stremio install URL.</summary>
    public string? InstallConfig { get; set; }
    /// <summary>v5 UID-only segment — the X-AniSync-Config credential (no revision).</summary>
    public string? ApiConfig { get; set; }
    public long Revision { get; set; }
    public string? AnimeService { get; set; }
    public string? Username { get; set; }
    public byte Flags1 { get; set; }
    public byte Flags2 { get; set; }
    public byte Flags3 { get; set; }
    public string? ScrobbleToken { get; set; }
    public string? PlexUsername { get; set; }
    public List<string> EnabledMediaTypes { get; set; } = new();
    public List<StreamAddonDto> StreamAddons { get; set; } = new();
    public List<LinkedSummaryDto> Linked { get; set; } = new();
}

public sealed class SaveFlagsResult { public long Revision { get; set; } public string? InstallConfig { get; set; } }
public sealed class RegenerateResult { public bool Ok { get; set; } public string? Config { get; set; } public string? InstallConfig { get; set; } }
public sealed class ScrobbleTokenDto { public string? Token { get; set; } }
public sealed class AddAddonResult { public bool Added { get; set; } public StreamAddonDto? Addon { get; set; } }
public sealed class OkResult { public bool Ok { get; set; } }
public sealed class DebridSkip { public string? Addon { get; set; } public string? Reason { get; set; } }
public sealed class DebridResult
{
    public List<StreamAddonDto> Added { get; set; } = new();
    public List<DebridSkip> Skipped { get; set; } = new();
    // Set (client-side only) when the request itself failed — non-2xx / network /
    // timeout — so the UI can show *why* instead of a generic "couldn't set up"
    // message. Null when the call reached the server and returned a result.
    public string? Error { get; set; }
}

public sealed class DebridProviderDto { public string? Id { get; set; } public string? Name { get; set; } public string? ApiKeyUrl { get; set; } public string? SignUpUrl { get; set; } }
public sealed class CatalogAddonDto { public string? Id { get; set; } public string? Name { get; set; } }
public sealed class StreamCatalogDto { public List<DebridProviderDto> Providers { get; set; } = new(); public List<CatalogAddonDto> Addons { get; set; } = new(); }

/// <summary>Request body for the one-click debrid setup.</summary>
public sealed class DebridSetupRequest
{
    public string? Provider { get; set; }
    public string? ApiKey { get; set; }
    public List<string>? Addons { get; set; }
}

/// <summary>
/// Named catalog + toggle flags, the client mirror of the server's
/// <c>ApplyBinaryFlags</c> bit layout (AniSync.Server/Utils.cs). Decodes the three
/// flag bytes into the configure page's checkboxes and re-encodes them on save.
/// Keeping the layout here (vs. shipping every bool over the wire) means the wire
/// shape stays three bytes and matches the addon's path-config format exactly.
/// </summary>
public sealed class ConfigFlags
{
    // flags1
    public bool ShowCurrent { get; set; }
    public bool ShowCompleted { get; set; }
    public bool ShowTrending { get; set; }
    public bool ShowSeasonal { get; set; }
    public bool DiscoverOnlyCurrent { get; set; }
    public bool DiscoverOnlyCompleted { get; set; }
    public bool DiscoverOnlyTrending { get; set; }
    public bool DiscoverOnlySeasonal { get; set; }
    // flags2
    public bool ShowPlanning { get; set; }
    public bool ShowPaused { get; set; }
    public bool ShowDropped { get; set; }
    public bool ShowRepeating { get; set; }
    public bool DiscoverOnlyPlanning { get; set; }
    public bool DiscoverOnlyPaused { get; set; }
    public bool DiscoverOnlyDropped { get; set; }
    public bool DiscoverOnlyRepeating { get; set; }
    // flags3
    public bool ShowAiring { get; set; }
    public bool ShowExternalStreams { get; set; }
    public bool HideManageEntry { get; set; }
    public bool DisableAutoTrack { get; set; }
    public bool DiscoverOnlyAiring { get; set; }
    public bool EnableSeasonGrouping { get; set; }
    public bool HideUnreleasedFromWatching { get; set; }
    public bool ShowAdultContent { get; set; }

    public static ConfigFlags Decode(byte f1, byte f2, byte f3) => new()
    {
        ShowCurrent = (f1 & 0x01) != 0,
        ShowCompleted = (f1 & 0x02) != 0,
        ShowTrending = (f1 & 0x04) != 0,
        ShowSeasonal = (f1 & 0x08) != 0,
        DiscoverOnlyCurrent = (f1 & 0x10) != 0,
        DiscoverOnlyCompleted = (f1 & 0x20) != 0,
        DiscoverOnlyTrending = (f1 & 0x40) != 0,
        DiscoverOnlySeasonal = (f1 & 0x80) != 0,
        ShowPlanning = (f2 & 0x01) != 0,
        ShowPaused = (f2 & 0x02) != 0,
        ShowDropped = (f2 & 0x04) != 0,
        ShowRepeating = (f2 & 0x08) != 0,
        DiscoverOnlyPlanning = (f2 & 0x10) != 0,
        DiscoverOnlyPaused = (f2 & 0x20) != 0,
        DiscoverOnlyDropped = (f2 & 0x40) != 0,
        DiscoverOnlyRepeating = (f2 & 0x80) != 0,
        ShowAiring = (f3 & 0x01) != 0,
        ShowExternalStreams = (f3 & 0x02) != 0,
        HideManageEntry = (f3 & 0x04) != 0,
        DisableAutoTrack = (f3 & 0x08) != 0,
        DiscoverOnlyAiring = (f3 & 0x10) != 0,
        EnableSeasonGrouping = (f3 & 0x20) != 0,
        HideUnreleasedFromWatching = (f3 & 0x40) != 0,
        ShowAdultContent = (f3 & 0x80) != 0,
    };

    public (byte f1, byte f2, byte f3) Encode()
    {
        byte f1 = 0, f2 = 0, f3 = 0;
        if (ShowCurrent) f1 |= 0x01;
        if (ShowCompleted) f1 |= 0x02;
        if (ShowTrending) f1 |= 0x04;
        if (ShowSeasonal) f1 |= 0x08;
        if (DiscoverOnlyCurrent) f1 |= 0x10;
        if (DiscoverOnlyCompleted) f1 |= 0x20;
        if (DiscoverOnlyTrending) f1 |= 0x40;
        if (DiscoverOnlySeasonal) f1 |= 0x80;
        if (ShowPlanning) f2 |= 0x01;
        if (ShowPaused) f2 |= 0x02;
        if (ShowDropped) f2 |= 0x04;
        if (ShowRepeating) f2 |= 0x08;
        if (DiscoverOnlyPlanning) f2 |= 0x10;
        if (DiscoverOnlyPaused) f2 |= 0x20;
        if (DiscoverOnlyDropped) f2 |= 0x40;
        if (DiscoverOnlyRepeating) f2 |= 0x80;
        if (ShowAiring) f3 |= 0x01;
        if (ShowExternalStreams) f3 |= 0x02;
        if (HideManageEntry) f3 |= 0x04;
        if (DisableAutoTrack) f3 |= 0x08;
        if (DiscoverOnlyAiring) f3 |= 0x10;
        if (EnableSeasonGrouping) f3 |= 0x20;
        if (HideUnreleasedFromWatching) f3 |= 0x40;
        if (ShowAdultContent) f3 |= 0x80;
        return (f1, f2, f3);
    }
}
