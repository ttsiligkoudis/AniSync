namespace AniSync.Client.Models;

// DTOs for the TV "sign in with your phone" device-pairing flow. JSON from the server is
// camelCase; System.Net.Http.Json's ReadFromJsonAsync uses web defaults (case-insensitive),
// so these PascalCase properties bind without attributes.

/// <summary>Response of POST /api/v1/auth/device/start — what the TV needs to show the QR.</summary>
public sealed class DeviceStartResponse
{
    /// <summary>Secret the TV polls with — never displayed.</summary>
    public string? DeviceCode { get; set; }
    /// <summary>Short human code shown on the TV (and carried in the QR's /link URL).</summary>
    public string? UserCode { get; set; }
    /// <summary>Base verification page, e.g. https://anisync.fly.dev/link.</summary>
    public string? VerificationUri { get; set; }
    /// <summary>Verification page with the code pre-filled — what the QR encodes.</summary>
    public string? VerificationUriComplete { get; set; }
    /// <summary>Ready-to-render PNG data URI of the QR.</summary>
    public string? QrPng { get; set; }
    /// <summary>Seconds the TV should wait between polls.</summary>
    public int Interval { get; set; }
    /// <summary>Seconds until the pairing expires.</summary>
    public int ExpiresIn { get; set; }
}

/// <summary>Response of POST /api/v1/auth/device/poll.</summary>
public sealed class DevicePollResponse
{
    /// <summary>"pending" | "approved" | "expired".</summary>
    public string? Status { get; set; }
    /// <summary>The config segment to store — present only when Status == "approved".</summary>
    public string? Config { get; set; }
}

/// <summary>Response of GET /api/v1/auth/device/context — drives the phone's /link page.</summary>
public sealed class DeviceContextResponse
{
    public bool Valid { get; set; }
    public bool SignedIn { get; set; }
}

/// <summary>Response of POST /api/v1/auth/handoff/start — the TV → phone settings handoff QR.</summary>
public sealed class SettingsHandoffResponse
{
    /// <summary>QR PNG (data: URI) the TV displays; encodes <see cref="Url"/>.</summary>
    public string? QrPng { get; set; }
    /// <summary>The /tv/handoff URL the phone opens to sign in + land on settings.</summary>
    public string? Url { get; set; }
    /// <summary>Token lifetime in seconds, so the TV can refresh the QR before it expires.</summary>
    public int ExpiresIn { get; set; }
}
