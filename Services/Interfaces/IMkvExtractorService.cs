using AnimeList.Models;

namespace AnimeList.Services.Interfaces
{
    /// <summary>
    /// Indexed Matroska subtitle extractor. The whole pipeline sits
    /// behind this one method so the implementation can be swapped
    /// without touching callers — currently <see cref="MkvExtractorService"/>
    /// runs the extraction in-process, but if we ever split it into
    /// its own Fly app a `RemoteMkvExtractorService` over HTTP slots
    /// in here with no client-side change.
    ///
    /// Contract:
    ///   * <paramref name="url"/> is the direct-CDN URL of the MKV file
    ///     (e.g. https://den1-4.download.real-debrid.com/d/.../file.mkv).
    ///     Implementations are expected to host-allowlist before fetching.
    ///   * <paramref name="lang"/> is "auto" (English-only, common case),
    ///     a specific ISO-639-2/B code, or null/empty for all tracks.
    ///   * The returned <see cref="MkvExtractResult"/>'s Extracted flag
    ///     tells the caller whether to use the result or fall through
    ///     to a streaming extractor.
    ///   * Cancellation is respected — long extractions abort cleanly
    ///     when the client disconnects.
    /// </summary>
    public interface IMkvExtractorService
    {
        Task<MkvExtractResult> ExtractAsync(string url, string? lang, CancellationToken ct);
    }
}
