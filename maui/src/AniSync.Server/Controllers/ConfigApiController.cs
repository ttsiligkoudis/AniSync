using AnimeList.Filters;
using AnimeList.Models;
using AnimeList.Models.Api;
using AnimeList.Services;
using AnimeList.Services.Interfaces;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace AnimeList.Controllers
{
    /// <summary>
    /// Header-authed port of the configure / account / advanced surface that the
    /// web app drives from the session-backed <see cref="HomeController"/>. The thin
    /// clients (Blazor Web + MAUI) have no server session, so each action resolves
    /// the caller's own row UID from the <c>X-AniSync-Config</c> header (never a
    /// body value) and mutates the config store directly. Endpoints that rotate the
    /// UID return the fresh config segment so the client can re-store its credential.
    ///
    /// <para>Only <b>stored (v5)</b> configs can be edited — an inline (v3) anonymous
    /// config has no server row, so these return 400, mirroring the existing
    /// preferences / sync-diff endpoints on <see cref="UserApiController"/>.</para>
    /// </summary>
    [ApiController]
    [Route("api/v1/me")]
    [RequireConfig]
    [IgnoreAntiforgeryToken]
    [EnableRateLimiting("api")]
    [Tags("User-scoped")]
    [Produces("application/json")]
    [ProducesResponseType(typeof(ApiError), StatusCodes.Status401Unauthorized)]
    public class ConfigApiController : ControllerBase
    {
        private readonly ITokenService _tokenService;
        private readonly IConfigStore _configStore;
        private readonly IAddonStreamService _addonStreamService;
        private readonly ILogger<ConfigApiController> _logger;

        public ConfigApiController(
            ITokenService tokenService,
            IConfigStore configStore,
            IAddonStreamService addonStreamService,
            ILogger<ConfigApiController> logger)
        {
            _tokenService = tokenService;
            _configStore = configStore;
            _addonStreamService = addonStreamService;
            _logger = logger;
        }

        private string ResolvedConfig => (string)HttpContext.Items[RequireConfigAttribute.ItemKey]!;

        /// <summary>
        /// Resolves the caller's stored row UID from their config header. Returns null
        /// for inline (v3) configs that have no server-side row.
        /// </summary>
        private async Task<string> ResolveUidAsync()
        {
            var cfg = await Utils.ResolveConfigAsync(ResolvedConfig, _configStore);
            return cfg?.tokenUid;
        }

        private IActionResult NotStored() =>
            BadRequest(new ApiError("config is not stored — this action needs a v5 (account) install URL"));

        // ── Full configure state (drives /configure, /account, /advanced) ─────────

        [HttpGet("config")]
        public async Task<IActionResult> GetConfig()
        {
            var uid = await ResolveUidAsync();
            if (string.IsNullOrEmpty(uid)) return NotStored();

            var token = await _configStore.GetAsync(uid);
            var (f1, f2, f3, revision) = await _configStore.GetFlagsAsync(uid);
            var linked = await _configStore.GetLinkedTokensAsync(uid);
            var scrobbleToken = await _configStore.EnsureScrobbleTokenAsync(uid);
            var plexUsername = await _configStore.GetPlexUsernameAsync(uid);
            var streamAddons = await _configStore.GetStreamAddonsAsync(uid);
            var enabledModes = await MediaTypePreference.ResolveEnabledAsync(HttpContext, uid, _configStore);

            return Ok(new
            {
                installConfig = Utils.EncodeV5Config(uid, revision),
                apiConfig = Utils.EncodeV5Config(uid),
                revision,
                animeService = token?.anime_service.ToString(),
                username = token?.username,
                flags1 = f1,
                flags2 = f2,
                flags3 = f3,
                scrobbleToken,
                plexUsername,
                enabledMediaTypes = enabledModes.Select(m => m.ToString()).ToList(),
                streamAddons = streamAddons.Select(a => new { url = a.Url, name = a.Name }).ToList(),
                linked = linked.Select(l => new { service = l.Service.ToString(), needsReauth = l.NeedsReauth }).ToList(),
            });
        }

        // ── Catalog / toggle flags ────────────────────────────────────────────────

        public sealed class SaveFlagsRequest
        {
            public byte flags1 { get; set; }
            public byte flags2 { get; set; }
            public byte flags3 { get; set; }
        }

        /// <summary>Persists the catalog + toggle bits and bumps the revision so the
        /// install URL changes (Stremio won't refetch an unchanged URL).</summary>
        [HttpPost("config/flags")]
        public async Task<IActionResult> SaveFlags([FromBody] SaveFlagsRequest body)
        {
            var uid = await ResolveUidAsync();
            if (string.IsNullOrEmpty(uid)) return NotStored();
            if (body == null) return BadRequest(new ApiError("missing body"));

            var revision = await _configStore.SetFlagsAsync(uid, body.flags1, body.flags2, body.flags3);
            return Ok(new { revision, installConfig = Utils.EncodeV5Config(uid, revision) });
        }

        /// <summary>Resets all toggle bits to zero (keeps the account); bumps revision.</summary>
        [HttpPost("config/reset")]
        public async Task<IActionResult> ResetConfig()
        {
            var uid = await ResolveUidAsync();
            if (string.IsNullOrEmpty(uid)) return NotStored();

            var revision = await _configStore.SetFlagsAsync(uid, 0, 0, 0);
            return Ok(new { revision, installConfig = Utils.EncodeV5Config(uid, revision) });
        }

        // ── Danger zone ─────────────────────────────────────────────────────────

        /// <summary>Deletes the config row — all install URLs / the header credential
        /// stop resolving. The client should clear its stored config after this.</summary>
        [HttpDelete("config")]
        public async Task<IActionResult> DeleteConfig()
        {
            var uid = await ResolveUidAsync();
            if (string.IsNullOrEmpty(uid)) return NotStored();

            await _configStore.DeleteAsync(uid);
            return Ok(new { ok = true });
        }

        /// <summary>Rotates the install UID (the leaked-UID recovery path), preserving all
        /// data. Returns the NEW config segment so the client re-stores its credential;
        /// the old one is now dead.</summary>
        [HttpPost("config/regenerate")]
        public async Task<IActionResult> RegenerateUid()
        {
            var uid = await ResolveUidAsync();
            if (string.IsNullOrEmpty(uid)) return NotStored();

            var newUid = await _configStore.RotateUidAsync(uid);
            if (string.IsNullOrEmpty(newUid)) return BadRequest(new ApiError("unknown uid"));

            var (_, _, _, revision) = await _configStore.GetFlagsAsync(newUid);
            return Ok(new
            {
                ok = true,
                config = Utils.EncodeV5Config(newUid),               // new X-AniSync-Config credential
                installConfig = Utils.EncodeV5Config(newUid, revision),
            });
        }

        /// <summary>Rotates the UID (invalidating every existing URL/credential everywhere)
        /// for a true "sign out on all devices". Same effect as regenerate from this
        /// device's standpoint: the client must clear its stored config.</summary>
        [HttpPost("signout-everywhere")]
        public async Task<IActionResult> SignOutEverywhere()
        {
            var uid = await ResolveUidAsync();
            if (string.IsNullOrEmpty(uid)) return NotStored();

            await _configStore.RotateUidAsync(uid);
            return Ok(new { ok = true });
        }

        // ── Linked accounts ───────────────────────────────────────────────────────

        /// <summary>Unlinks a secondary provider from the caller's row. (OAuth *linking*
        /// needs a browser redirect and stays on /Auth/LinkProvider; this removal is a
        /// pure store mutation, so it's header-authed here.)</summary>
        [HttpPost("unlink/{service}")]
        public async Task<IActionResult> Unlink(AnimeService service)
        {
            var uid = await ResolveUidAsync();
            if (string.IsNullOrEmpty(uid)) return NotStored();

            await _configStore.RemoveLinkedTokenAsync(uid, service);
            return Ok(new { ok = true });
        }

        // ── Home-server sync (scrobble webhook + Plex username) ───────────────────

        [HttpGet("scrobble")]
        public async Task<IActionResult> GetScrobble()
        {
            var uid = await ResolveUidAsync();
            if (string.IsNullOrEmpty(uid)) return NotStored();
            var token = await _configStore.EnsureScrobbleTokenAsync(uid);
            return Ok(new { token });
        }

        [HttpPost("scrobble/rotate")]
        public async Task<IActionResult> RotateScrobble()
        {
            var uid = await ResolveUidAsync();
            if (string.IsNullOrEmpty(uid)) return NotStored();
            var token = await _configStore.RotateScrobbleTokenAsync(uid);
            if (string.IsNullOrEmpty(token)) return BadRequest(new ApiError("unknown uid"));
            return Ok(new { token });
        }

        public sealed class PlexUsernameBody { public string username { get; set; } }

        [HttpPost("plex-username")]
        public async Task<IActionResult> SetPlexUsername([FromBody] PlexUsernameBody body)
        {
            var uid = await ResolveUidAsync();
            if (string.IsNullOrEmpty(uid)) return NotStored();
            await _configStore.SetPlexUsernameAsync(uid, body?.username);
            return Ok(new { ok = true });
        }

        // ── Stream addons ─────────────────────────────────────────────────────────

        [HttpGet("stream-addons")]
        public async Task<IActionResult> ListStreamAddons()
        {
            var uid = await ResolveUidAsync();
            if (string.IsNullOrEmpty(uid)) return NotStored();
            var addons = await _configStore.GetStreamAddonsAsync(uid);
            return Ok(new { addons = addons.Select(a => new { url = a.Url, name = a.Name }).ToList() });
        }

        [HttpPost("stream-addons")]
        public async Task<IActionResult> AddStreamAddon([FromBody] StreamAddonRequest request)
        {
            var uid = await ResolveUidAsync();
            if (string.IsNullOrEmpty(uid)) return NotStored();
            var manifestUrl = (request?.manifestUrl ?? string.Empty).Trim();
            if (string.IsNullOrEmpty(manifestUrl))
                return BadRequest(new ApiError("manifest URL required"));

            var addon = await _addonStreamService.FetchManifestAsync(manifestUrl);
            if (addon == null)
                return BadRequest(new ApiError("couldn't fetch a Stremio stream-addon manifest at that URL"));

            // First-addon transition: once any stream addon exists, external streaming-site
            // links default off — but only on the 0→1 add (a later re-enable is respected).
            var hadAddons = (await _configStore.GetStreamAddonsAsync(uid)).Count > 0;
            var added = await _configStore.AddStreamAddonAsync(uid, addon);
            if (added && !hadAddons)
                await _configStore.ClearShowExternalStreamsAsync(uid);

            return Ok(new { added, addon = new { url = addon.Url, name = addon.Name } });
        }

        [HttpDelete("stream-addons")]
        public async Task<IActionResult> RemoveStreamAddon([FromBody] StreamAddonRequest request)
        {
            var uid = await ResolveUidAsync();
            if (string.IsNullOrEmpty(uid)) return NotStored();
            var manifestUrl = (request?.manifestUrl ?? string.Empty).Trim();
            if (string.IsNullOrEmpty(manifestUrl))
                return BadRequest(new ApiError("manifest URL required"));

            var removed = await _configStore.RemoveStreamAddonAsync(uid, manifestUrl);
            return Ok(new { removed });
        }

        [HttpPost("stream-addons/reorder")]
        public async Task<IActionResult> ReorderStreamAddons([FromBody] ReorderStreamAddonsRequest request)
        {
            var uid = await ResolveUidAsync();
            if (string.IsNullOrEmpty(uid)) return NotStored();
            if (request?.urls == null || request.urls.Count == 0)
                return BadRequest(new ApiError("urls required"));

            var changed = await _configStore.ReorderStreamAddonsAsync(uid, request.urls);
            return Ok(new { changed });
        }

        /// <summary>One-click debrid setup — ports HomeController.AddDebridAddons: builds
        /// each catalog addon's manifest URL from the provider + API key, validates it the
        /// same way the manual add does, and persists the ones that check out.</summary>
        [HttpPost("stream-addons/debrid")]
        public async Task<IActionResult> AddDebridAddons([FromBody] DebridAddonsRequest request)
        {
            var uid = await ResolveUidAsync();
            if (string.IsNullOrEmpty(uid)) return NotStored();

            var provider = StreamAddonCatalog.FindProvider(request?.provider);
            if (provider == null) return BadRequest(new ApiError("unknown debrid provider"));
            var apiKey = (request?.apiKey ?? string.Empty).Trim();
            if (string.IsNullOrEmpty(apiKey)) return BadRequest(new ApiError("API key required"));

            var addonIds = (request.addons != null && request.addons.Count > 0)
                ? request.addons
                : StreamAddonCatalog.Addons.Select(a => a.Id).ToList();

            var hadAddons = (await _configStore.GetStreamAddonsAsync(uid)).Count > 0;
            var added = new List<object>();
            var skipped = new List<object>();

            // Phase 1 — resolve + validate every addon's manifest(s) CONCURRENTLY. This is
            // all network-bound (MediaFusion's encrypt POST + each manifest fetch, up to a
            // handful of 8s-timeout calls). Run sequentially they stacked into tens of
            // seconds — long enough to trip a gateway timeout or stall the calling Blazor
            // circuit, which surfaced to the user as a generic "couldn't set up addons".
            // FetchManifestAsync / EncryptConfigAsync are stateless and use
            // IHttpClientFactory, so they're safe to fan out.
            var resolutions = await Task.WhenAll(addonIds.Select(ResolveAddonAsync));

            // Phase 2 — persist sequentially in the requested order. AddStreamAddonAsync is
            // a read-modify-write on the same row, so these must NOT interleave.
            foreach (var (addonId, validated, skipReason) in resolutions)
            {
                if (validated.Count == 0)
                {
                    skipped.Add(new { addon = addonId, reason = skipReason });
                    continue;
                }
                foreach (var addon in validated)
                {
                    var wasAdded = await _configStore.AddStreamAddonAsync(uid, addon);
                    if (wasAdded) added.Add(new { url = addon.Url, name = addon.Name });
                    else skipped.Add(new { addon = addonId, reason = "already added" });
                }
            }

            if (added.Count > 0 && !hadAddons)
                await _configStore.ClearShowExternalStreamsAsync(uid);

            return Ok(new { added, skipped });

            // Builds an addon's manifest URL(s) and validates each by fetching it, exactly
            // as the manual Add path does. Returns the validated addons (empty + a reason
            // when nothing checks out) without touching the store.
            async Task<(string AddonId, IReadOnlyList<StreamAddon> Validated, string? SkipReason)> ResolveAddonAsync(string addonId)
            {
                if (!StreamAddonCatalog.IsKnownAddon(addonId))
                    return (addonId, Array.Empty<StreamAddon>(), "unknown addon");

                IReadOnlyList<string> manifestUrls;
                if (string.Equals(addonId, "mediafusion", StringComparison.OrdinalIgnoreCase))
                {
                    var configJson = StreamAddonCatalog.BuildMediaFusionConfigJson(provider, apiKey);
                    var token = configJson == null
                        ? null
                        : await _addonStreamService.EncryptConfigAsync(StreamAddonCatalog.MediaFusionEncryptUrl, configJson);
                    manifestUrls = string.IsNullOrEmpty(token)
                        ? Array.Empty<string>()
                        : new[] { $"{StreamAddonCatalog.MediaFusionHost}/{token}/manifest.json" };
                }
                else
                {
                    manifestUrls = StreamAddonCatalog.BuildManifestUrls(addonId, provider, apiKey);
                }

                if (manifestUrls.Count == 0)
                    return (addonId, Array.Empty<StreamAddon>(), "couldn't build a manifest URL");

                var fetched = await Task.WhenAll(manifestUrls.Select(u => _addonStreamService.FetchManifestAsync(u)));
                var valid = fetched.Where(a => a != null).Select(a => a!).ToList();
                return valid.Count == 0
                    ? (addonId, Array.Empty<StreamAddon>(), "couldn't validate — check your key, or add it manually")
                    : (addonId, (IReadOnlyList<StreamAddon>)valid, null);
            }
        }

        // ── Backups ───────────────────────────────────────────────────────────────

        /// <summary>Returns the exportable backup (tokens + flags) as JSON. The client
        /// turns it into a downloadable file (the header credential can't ride on a plain
        /// anchor download, so the UI fetches + builds the Blob itself).</summary>
        [HttpGet("export")]
        public async Task<IActionResult> Export()
        {
            var uid = await ResolveUidAsync();
            if (string.IsNullOrEmpty(uid)) return NotStored();
            var token = await _configStore.GetAsync(uid);
            if (token == null || token.anonymousUser) return NotStored();

            var (f1, f2, f3, _) = await _configStore.GetFlagsAsync(uid);
            return Ok(new ConfigBackup
            {
                version = 1,
                service = token.anime_service.ToString(),
                tokenData = token,
                flags = new BackupFlags { flags1 = f1, flags2 = f2, flags3 = f3 },
            });
        }

        /// <summary>Restores an exported backup: writes its tokens + flags into the store
        /// and returns the resulting config segment so the client adopts it as its
        /// credential (no OAuth re-login needed).</summary>
        [HttpPost("import")]
        public async Task<IActionResult> Import([FromBody] ConfigBackup backup)
        {
            if (backup?.tokenData == null)
                return BadRequest(new ApiError("backup file is missing tokenData"));

            var uid = await _configStore.UpsertAsync(backup.tokenData);
            if (backup.flags != null)
                await _configStore.SetFlagsAsync(uid, backup.flags.flags1, backup.flags.flags2, backup.flags.flags3);

            var (_, _, _, revision) = await _configStore.GetFlagsAsync(uid);
            return Ok(new
            {
                ok = true,
                config = Utils.EncodeV5Config(uid),
                installConfig = Utils.EncodeV5Config(uid, revision),
            });
        }
    }
}
