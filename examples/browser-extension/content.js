// AniSync auto-tracker content script.
//
// Detection pipeline:
//   1. Per-site adapter (registry below) — first match wins. Adapters return
//      null when they don't recognise the current page (homepages, settings),
//      which falls through to the universal chain so navigation around a
//      streaming site doesn't fire spurious detections.
//   2. Universal heuristics — JSON-LD Schema.org, OpenGraph meta, document
//      title regex, URL slug, DOM h1 fallback. Same as before.
//
// Once a detection lands, the floating "Track" button is injected (manual
// confirm + manual save fallback for sites the heuristics miss), a
// MutationObserver attaches to whatever <video> shows up, and SPA route
// changes are handled by patching history.pushState — most modern streaming
// sites change episodes without a page reload, which would otherwise leave
// the auto-fire flag stuck on the previous episode.

(function () {
  'use strict';

  // Hoisted state — declared up here so SPA navigation, MutationObserver,
  // and the floating UI can all touch the same lastDetection / autoFired.
  let injected = false;
  let lastDetection = null;
  let autoFired = false;
  let uiPaint = null;        // set by injectUi so onSpaNavigate can re-paint
  let attachedVideo = null;  // the <video> the timeupdate listener is on

  // Whether the script is running under a persistent (manifest-declared or
  // user-opted-in) host permission, vs an activeTab one-shot injection. Set
  // asynchronously after a round-trip to background — until then the floating
  // UI shows nothing about persistence so we don't flicker the CTA.
  let permissionState = { persistent: null, pattern: null };
  let uiUpdatePermission = null;  // hoisted so the round-trip can re-paint

  chrome.runtime.sendMessage({ type: 'anisync:check-permission' }, (resp) => {
    if (chrome.runtime.lastError) return;
    permissionState = resp || { persistent: false, pattern: null };
    if (uiUpdatePermission) uiUpdatePermission(permissionState);
  });

  // ── Site adapter registry ─────────────────────────────────────────────
  // Adapter shape: { hostnames: (string|RegExp)[], extract(): {title, episode, source}|null }.
  // Order matters — the first matching adapter wins. Adapters live below
  // the universal extractors so they can call them as helpers; the registry
  // is built lazily inside detect().

  function hostnameMatches(adapter) {
    const h = location.hostname;
    return adapter.hostnames.some(p =>
      typeof p === 'string' ? (h === p || h.endsWith('.' + p)) : p.test(h));
  }

  function activeAdapter() {
    for (const a of ADAPTERS) if (hostnameMatches(a)) return a;
    return null;
  }

  // ── Universal heuristic extractors ────────────────────────────────────
  // Each returns { title, episode } on success or null. Used directly when
  // no adapter matches, and called by adapters that want to delegate to a
  // specific extractor (e.g. Crunchyroll just wants JSON-LD).

  function extractFromJsonLd() {
    for (const tag of document.querySelectorAll('script[type="application/ld+json"]')) {
      try {
        const parsed = JSON.parse(tag.textContent);
        const items = Array.isArray(parsed) ? parsed : [parsed];
        for (const item of items) {
          if (!item || typeof item !== 'object') continue;
          const type = item['@type'];
          if (type !== 'TVEpisode' && type !== 'Episode') continue;
          const series = item.partOfSeries?.name
                      || item.partOfTVSeries?.name
                      || item.partOfSeason?.partOfSeries?.name;
          const episode = Number(item.episodeNumber);
          if (series && Number.isFinite(episode) && episode > 0) {
            return { title: series, episode, source: 'json-ld' };
          }
        }
      } catch { /* malformed JSON-LD — skip */ }
    }
    return null;
  }

  function extractFromMeta() {
    const get = (sel) => document.querySelector(sel)?.content?.trim();
    const series = get('meta[property="og:video:series"]')
                || get('meta[property="video:series"]');
    const ogTitle = get('meta[property="og:title"]') || get('meta[name="twitter:title"]');
    const epRaw = get('meta[property="og:video:episode"]')
               || get('meta[property="video:episode"]')
               || get('meta[name="episode"]');
    const episode = Number(epRaw);
    if (series && Number.isFinite(episode) && episode > 0) {
      return { title: series, episode, source: 'og-meta' };
    }
    if (ogTitle && !series) {
      const parsed = parseTitleString(ogTitle);
      if (parsed) return { ...parsed, source: 'og-title' };
    }
    return null;
  }

  // Tries common "Show — Episode N" shapes against an arbitrary string. Used
  // by both the document.title path and the OG-title fallback.
  function parseTitleString(s) {
    if (!s) return null;
    const patterns = [
      /^(.+?)\s*[-–—|:]\s*Episode\s*(\d+)/i,
      /^(.+?)\s*[-–—|:]\s*Ep\.?\s*(\d+)/i,
      /^(.+?)\s*S\d+\s*E(\d+)/i,
      /^(.+?)\s+Episode\s*(\d+)/i,
      /Episode\s*(\d+)\s*[-–—|:]\s*(.+?)$/i,
    ];
    for (const p of patterns) {
      const m = s.match(p);
      if (!m) continue;
      const isEpisodeFirst = /^Episode/i.test(p.source);
      const title = (isEpisodeFirst ? m[2] : m[1]).trim();
      const episode = Number(isEpisodeFirst ? m[1] : m[2]);
      if (title && Number.isFinite(episode) && episode > 0) {
        return { title, episode };
      }
    }
    return null;
  }

  function extractFromDocumentTitle() {
    const parsed = parseTitleString(document.title);
    return parsed ? { ...parsed, source: 'document-title' } : null;
  }

  // Many anime streaming sites encode { title, episode } directly in the URL
  // path — e.g. /daemons-of-the-shadow-realm-episode-1-english-subbed/. Often
  // the only reliable signal when the actual <video> lives in a cross-origin
  // iframe and the parent DOM has nothing useful. Cheap and runs first.
  function extractFromUrl() {
    const path = decodeURIComponent(location.pathname).toLowerCase();
    const patterns = [
      /\/([^/]+?)-episode-(\d+)\b/,
      /\/([^/]+?)-ep-(\d+)\b/,
      /\/([^/]+?)\/episode-(\d+)\b/,
      /\/([^/]+?)\/ep-(\d+)\b/,
      /\/watch\/([^/]+?)\/(\d+)\b/,
    ];
    for (const p of patterns) {
      const m = path.match(p);
      if (!m) continue;
      const slug = m[1];
      const episode = Number(m[2]);
      if (!slug || !Number.isFinite(episode) || episode <= 0) continue;
      const title = cleanScrapedTitle(slug.replace(/-/g, ' '));
      if (title) return { title, episode, source: 'url-slug' };
    }
    return null;
  }

  function extractFromDom() {
    // Last-ditch: pull the most prominent heading and look for an episode
    // number nearby. Loose and false-positive-prone, which is why the
    // floating button always lets the user correct.
    const h1 = document.querySelector('h1')?.textContent?.trim();
    if (!h1) return null;
    const epSource = h1 + ' ' + document.title;
    const m = epSource.match(/Episode\s*(\d+)|Ep\.?\s*(\d+)|\bE(\d+)\b/i);
    if (!m) return null;
    const episode = Number(m[1] ?? m[2] ?? m[3]);
    if (!Number.isFinite(episode) || episode <= 0) return null;
    const title = h1.replace(/Episode\s*\d+|Ep\.?\s*\d+|\bE\d+\b/i, '').trim();
    return title ? { title, episode, source: 'dom-h1' } : null;
  }

  // Strips the noise-words streaming sites tack onto titles ("english subbed",
  // "1080p", "raw", …) and title-cases the result.
  function cleanScrapedTitle(s) {
    s = s.replace(/\b(english|sub|subbed|dub|dubbed|raw|hd|fhd|sd|1080p|720p|480p)\b/gi, ' ');
    s = s.replace(/\s+/g, ' ').trim();
    return s.replace(/\b\w/g, c => c.toUpperCase());
  }

  // ── Concrete site adapters ────────────────────────────────────────────
  // Each delegates to the universal extractors when the site already
  // publishes good metadata; the win is being able to short-circuit on
  // pages that aren't watch pages and to apply site-specific selectors
  // when generic heuristics miss.

  const ADAPTERS = [
    // Crunchyroll. SPA on the watch page; JSON-LD TVEpisode is reliably
    // populated. Document.title format is "Show - Episode N - Episode Title
    // - Watch on Crunchyroll" so we have a deterministic fallback when
    // JSON-LD is delayed-loaded by the SPA.
    {
      hostnames: ['crunchyroll.com'],
      extract() {
        // Locale prefix on the path: /en/watch/{id}/{slug}, /es-419/watch/...
        if (!/^\/[a-z-]+\/watch\//i.test(location.pathname)) return null;

        const ld = extractFromJsonLd();
        if (ld) return { ...ld, source: 'crunchyroll-jsonld' };

        const m = document.title.match(/^(.+?)\s*-\s*Episode\s*(\d+)/i);
        if (m) return { title: m[1].trim(), episode: Number(m[2]), source: 'crunchyroll-title' };

        return null;
      },
    },

    // HiAnime / Zoro / aniwatch (same engine, multiple TLDs as the host
    // domain rotates). Episode number lives only in the sidebar — the URL
    // uses an opaque ?ep=12345 internal id. The sidebar's active item gets
    // one of several class variants depending on the theme version.
    {
      hostnames: [
        'hianime.to', 'hianime.tv', 'hianime.nz', 'hianime.bz',
        'aniwatch.to', 'aniwatchtv.to',
        'zoro.to', 'zoro.in',
      ],
      extract() {
        if (!location.pathname.startsWith('/watch/')) return null;

        const active = document.querySelector(
          '.ep-item.active, .ssl-item.ssl-item-active, .ssl-item.active'
        );
        if (!active) return null;

        // Episode number sits in .ssli-order or as data-number on the link.
        const epRaw = active.querySelector('.ssli-order')?.textContent
                   || active.getAttribute('data-number')
                   || active.textContent;
        const episode = Number(String(epRaw || '').match(/\d+/)?.[0]);
        if (!Number.isFinite(episode) || episode <= 0) return null;

        const title = document.querySelector('.film-name a, h2.film-name')?.textContent?.trim()
                   || document.querySelector('h1, h2')?.textContent?.trim();
        if (!title) return null;

        return { title, episode, source: 'hianime-sidebar' };
      },
    },

    // Aniwave (formerly 9anime). Watch URLs are /watch/SLUG.UUID/EP_ID.
    // Episode list is /ul.episodes a/, active marker on the current item.
    {
      hostnames: [
        'aniwave.to', 'aniwave.li', 'aniwave.bz', 'aniwave.cx', 'aniwave.se',
      ],
      extract() {
        if (!location.pathname.startsWith('/watch/')) return null;

        const active = document.querySelector(
          'ul.episodes a.active, .episodes .active, [data-active="1"]'
        );
        if (!active) return null;

        const epRaw = active.querySelector('.d-title')?.textContent
                   || active.getAttribute('data-num')
                   || active.textContent;
        const episode = Number(String(epRaw || '').match(/\d+/)?.[0]);
        if (!Number.isFinite(episode) || episode <= 0) return null;

        const title = document.querySelector('.info h1, .film-info h1, h1.title')
                        ?.textContent?.trim();
        if (!title) return null;

        return { title, episode, source: 'aniwave-sidebar' };
      },
    },
  ];

  // ── Detection orchestrator ────────────────────────────────────────────

  function detect() {
    const adapter = activeAdapter();
    if (adapter) {
      const adapterResult = adapter.extract();
      if (adapterResult) return adapterResult;
    }
    // Universal fallback chain, ordered cheapest-first. URL slug is faster
    // than parsing arbitrary JSON-LD blobs, JSON-LD is more reliable than
    // OG meta, OG meta is more reliable than the document title regex,
    // and DOM h1 is the loosest catch-all.
    return extractFromUrl()
        || extractFromJsonLd()
        || extractFromMeta()
        || extractFromDocumentTitle()
        || extractFromDom();
  }

  // Heuristic for "is this an episode page?" so the floating button doesn't
  // appear on every site. Adapters short-circuit when their site is matched
  // (they return non-null on watch pages, null elsewhere); the universal
  // checks cover sites without a dedicated adapter.
  function shouldShowUi() {
    if (activeAdapter()?.extract()) return true;
    return !!(extractFromUrl() || extractFromJsonLd() || extractFromMeta() || extractFromDocumentTitle());
  }

  // ── SPA navigation handling ───────────────────────────────────────────
  // Modern streaming sites change episode without a full page reload. The
  // pushState patch fires a custom event we listen to, so detection re-runs
  // and the auto-fire flag clears when the (title, episode) actually changes.
  // Without this, ep 7's playback would silently swallow the auto-save
  // because autoFired was set during ep 6.

  (function patchHistoryForSpa() {
    const dispatch = () => window.dispatchEvent(new Event('anisync:locationchange'));
    const origPush = history.pushState;
    history.pushState = function (...args) {
      const r = origPush.apply(this, args);
      dispatch();
      return r;
    };
    const origReplace = history.replaceState;
    history.replaceState = function (...args) {
      const r = origReplace.apply(this, args);
      dispatch();
      return r;
    };
    window.addEventListener('popstate', dispatch);
  })();

  function onSpaNavigate() {
    const fresh = detect();
    if (!fresh) {
      // Navigated off an episode page — reset detection so the next episode
      // starts clean. Don't clear autoFired here; the video is presumably
      // about to be torn down anyway.
      lastDetection = null;
      return;
    }
    if (!lastDetection
        || fresh.title !== lastDetection.title
        || fresh.episode !== lastDetection.episode) {
      autoFired = false;
      lastDetection = fresh;
      if (uiPaint) uiPaint(fresh);
    }
  }

  window.addEventListener('anisync:locationchange', onSpaNavigate);

  // ── Floating confirm UI ───────────────────────────────────────────────
  // Tiny self-contained widget with high z-index so it floats over arbitrary
  // host pages. No shadow DOM (keeps the example readable); class names are
  // unique enough to avoid clashes in practice.

  const STYLE = `
    .anisync-fab {
      position: fixed; bottom: 20px; right: 20px;
      z-index: 2147483647;
      font-family: system-ui, -apple-system, sans-serif;
      font-size: 13px;
      color: #fff;
    }
    .anisync-fab-pill {
      background: #7B5BF5; border: none; border-radius: 999px;
      padding: 10px 16px; font-size: 13px; font-weight: 600;
      box-shadow: 0 4px 12px rgba(0,0,0,0.3);
      cursor: pointer; display: flex; align-items: center; gap: 6px;
    }
    .anisync-fab-pill:hover { background: #6849e0; }
    .anisync-fab-card {
      background: #1f1f24; border-radius: 12px; padding: 14px;
      box-shadow: 0 8px 28px rgba(0,0,0,0.5);
      width: 280px;
    }
    .anisync-fab-card h3 { margin: 0 0 10px; font-size: 14px; font-weight: 600; }
    .anisync-fab-card label { display: block; font-size: 11px; opacity: 0.7; margin-top: 8px; }
    .anisync-fab-card input {
      width: 100%; box-sizing: border-box;
      background: #2a2a31; border: 1px solid #3a3a44; color: #fff;
      border-radius: 6px; padding: 6px 8px; font-size: 13px; margin-top: 4px;
    }
    .anisync-fab-row { display: flex; gap: 8px; margin-top: 12px; }
    .anisync-fab-row button {
      flex: 1; padding: 8px; border-radius: 6px; border: none;
      font-size: 12px; font-weight: 600; cursor: pointer;
    }
    .anisync-fab-save  { background: #7B5BF5; color: #fff; }
    .anisync-fab-close { background: #2a2a31; color: #ccc; }
    .anisync-fab-status { margin-top: 8px; font-size: 11px; opacity: 0.8; min-height: 14px; }
    .anisync-fab-source { font-size: 10px; opacity: 0.5; margin-top: 4px; }
    .anisync-fab-allow {
      display: none;
      background: rgba(123, 91, 245, 0.12);
      border: 1px solid rgba(123, 91, 245, 0.4);
      border-radius: 8px; padding: 10px;
      margin: 0 0 10px;
    }
    .anisync-fab-allow strong { display: block; font-size: 12px; margin-bottom: 4px; }
    .anisync-fab-allow span { display: block; font-size: 11px; opacity: 0.75; margin-bottom: 8px; }
    .anisync-fab-allow button {
      width: 100%; padding: 7px; border: none; border-radius: 6px;
      background: #7B5BF5; color: #fff; font-size: 12px; font-weight: 600;
      cursor: pointer;
    }
    .anisync-fab-allow button:disabled { opacity: 0.6; cursor: default; }
  `;

  function injectUi(detection) {
    if (injected) return;
    injected = true;

    const style = document.createElement('style');
    style.textContent = STYLE;
    document.head.appendChild(style);

    const root = document.createElement('div');
    root.className = 'anisync-fab';
    root.innerHTML = `
      <button class="anisync-fab-pill" type="button">📺 Track</button>
      <div class="anisync-fab-card" hidden>
        <h3>Track this episode</h3>
        <div class="anisync-fab-allow">
          <strong>Tracking is one-shot on this site</strong>
          <span>Allow AniSync on <span class="anisync-fab-allow-host"></span> to auto-track future episodes without clicking the toolbar each time.</span>
          <button class="anisync-fab-allow-btn" type="button">Always allow on this site</button>
        </div>
        <label>Show title</label>
        <input class="anisync-fab-title" />
        <label>Episode</label>
        <input class="anisync-fab-episode" type="number" min="1" />
        <div class="anisync-fab-source"></div>
        <div class="anisync-fab-row">
          <button class="anisync-fab-close" type="button">Close</button>
          <button class="anisync-fab-save"  type="button">Save</button>
        </div>
        <div class="anisync-fab-status"></div>
      </div>
    `;
    document.documentElement.appendChild(root);

    const $pill = root.querySelector('.anisync-fab-pill');
    const $card = root.querySelector('.anisync-fab-card');
    const $title = root.querySelector('.anisync-fab-title');
    const $episode = root.querySelector('.anisync-fab-episode');
    const $source = root.querySelector('.anisync-fab-source');
    const $status = root.querySelector('.anisync-fab-status');
    const $save = root.querySelector('.anisync-fab-save');
    const $close = root.querySelector('.anisync-fab-close');
    const $allow = root.querySelector('.anisync-fab-allow');
    const $allowHost = root.querySelector('.anisync-fab-allow-host');
    const $allowBtn = root.querySelector('.anisync-fab-allow-btn');

    function paint(d) {
      $title.value = d?.title ?? '';
      $episode.value = d?.episode ?? '';
      $source.textContent = d?.source
        ? `Detected via ${d.source}`
        : 'No detection — fill in manually.';
    }
    paint(detection);
    uiPaint = paint;  // exposed so onSpaNavigate can re-paint after a route change

    // Permission CTA — visible only when running under a transient activeTab
    // injection on a host the user hasn't opted into. Hidden once the host
    // gets a persistent permission so a refresh of the page or future visits
    // are silent.
    function paintPermission(state) {
      if (state?.persistent) {
        $allow.style.display = 'none';
      } else {
        $allow.style.display = 'block';
        $allowHost.textContent = location.hostname;
      }
    }
    paintPermission(permissionState);
    uiUpdatePermission = paintPermission;

    $allowBtn.addEventListener('click', () => {
      // The click here is the user gesture that lets the background worker
      // call chrome.permissions.request — Chrome's permission dialog is gated
      // on a fresh user gesture and the message chain preserves it within a
      // few-second window.
      $allowBtn.disabled = true;
      $allowBtn.textContent = 'Waiting for Chrome…';
      chrome.runtime.sendMessage({
        type: 'anisync:request-permission',
        pattern: permissionState?.pattern,  // background falls back to sender.tab.url if missing
      }, (resp) => {
        if (chrome.runtime.lastError || !resp?.ok) {
          $allowBtn.disabled = false;
          $allowBtn.textContent = 'Always allow on this site';
          $status.textContent = 'Permission ' + (resp?.error || chrome.runtime.lastError?.message || 'denied');
          return;
        }
        permissionState = { persistent: true, pattern: resp.pattern };
        paintPermission(permissionState);
        $status.textContent = 'AniSync now auto-tracks ' + location.hostname + '.';
      });
    });

    $pill.addEventListener('click', () => {
      $card.hidden = !$card.hidden;
      $pill.hidden = !$card.hidden;
      // Re-detect every time the user opens the card; the page's DOM may
      // have changed (SPA navigation, lazy-loaded metadata).
      const fresh = detect();
      if (fresh) { lastDetection = fresh; paint(fresh); }
    });

    $close.addEventListener('click', () => {
      $card.hidden = true;
      $pill.hidden = false;
      $status.textContent = '';
    });

    $save.addEventListener('click', () => {
      const title = $title.value.trim();
      const episode = Number($episode.value);
      if (!title || !Number.isFinite(episode) || episode <= 0) {
        $status.textContent = 'Need a title and a positive episode number.';
        return;
      }
      $status.textContent = 'Saving…';
      chrome.runtime.sendMessage({
        type: 'anisync:watched',
        title,
        episode,
        source: location.hostname,
        manual: true,
      }, (response) => {
        if (chrome.runtime.lastError) {
          $status.textContent = 'Failed: ' + chrome.runtime.lastError.message;
          return;
        }
        $status.textContent = response?.ok
          ? `Saved → ${response.name ?? title} ep ${episode}`
          : `Failed: ${response?.error ?? 'unknown error'}`;
      });
    });
  }

  // ── Video watcher (MutationObserver-driven) ───────────────────────────
  // Replaces the old setTimeout polling. The observer fires synchronously
  // when a <video> is added (immediate vs the previous 1.5s floor) and
  // when the SPA player swaps src on the same element across episodes.

  function attachToVideo(video) {
    if (attachedVideo === video) return;
    attachedVideo = video;
    autoFired = false;

    video.addEventListener('timeupdate', () => {
      if (autoFired || !video.duration || !lastDetection) return;
      const pct = video.currentTime / video.duration;
      if (pct < 0.8) return;
      autoFired = true;

      chrome.runtime.sendMessage({
        type: 'anisync:watched',
        title: lastDetection.title,
        episode: lastDetection.episode,
        source: location.hostname,
      });
    });

    // Some SPA players keep one <video> across episodes by swapping src.
    // loadedmetadata fires on each new source, giving us a clean reset
    // hook even when the DOM element doesn't change.
    video.addEventListener('loadedmetadata', () => {
      autoFired = false;
    });
  }

  function bootstrap() {
    lastDetection = detect();
    if (!injected && shouldShowUi()) injectUi(lastDetection);

    const existing = document.querySelector('video');
    if (existing) attachToVideo(existing);

    // Observe the document for any DOM mutations: new <video>, new JSON-LD
    // tags injected by the SPA after route change, sidebar updates that
    // unmask the active episode. Cheap because we only run extraction
    // when there isn't already a detection or the current adapter signals
    // the page changed shape.
    const obs = new MutationObserver(() => {
      const video = document.querySelector('video');
      if (video && video !== attachedVideo) attachToVideo(video);

      if (!lastDetection) {
        const fresh = detect();
        if (fresh) {
          lastDetection = fresh;
          if (!injected && shouldShowUi()) injectUi(fresh);
          else if (uiPaint) uiPaint(fresh);
        }
      }
    });
    obs.observe(document.body || document.documentElement, {
      childList: true, subtree: true,
    });
  }

  if (document.body) bootstrap();
  else document.addEventListener('DOMContentLoaded', bootstrap, { once: true });
})();
