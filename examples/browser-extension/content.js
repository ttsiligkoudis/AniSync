// AniSync auto-tracker content script — universal mode.
//
// Runs on every page (manifest matches <all_urls>) and is a no-op unless it
// finds a <video> element. When one shows up:
//   - tries a chain of heuristics to extract { title, episode } from the page
//     (JSON-LD Schema.org, OpenGraph meta, document.title regex, DOM h1
//     breadcrumb fallback)
//   - injects a small floating "Track" button bottom-right with the detected
//     values pre-filled so the user can confirm or correct before saving
//   - if the heuristic extraction succeeded, auto-fires the save once the
//     video crosses 80% playback (same as before)
//
// The floating button is the failsafe for sites where the heuristics miss —
// the user can type the title + episode manually and hit Save.

(function () {
  'use strict';

  // ── Heuristic extractors ──────────────────────────────────────────────
  // Each returns { title, episode } on success or null. Walked in order;
  // first hit wins. JSON-LD is most reliable (Schema.org TVEpisode is well-
  // defined), DOM is the last-resort fallback.

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
    // OG title alone sometimes encodes "Show — Episode N"; let the title
    // regex stage catch that — keep this branch focused on structured data.
    if (ogTitle && !series) {
      const parsed = parseTitleString(ogTitle);
      if (parsed) return { ...parsed, source: 'og-title' };
    }
    return null;
  }

  // Tries common "Show — Episode N" shapes against an arbitrary string.
  // Used by both the document.title path and the OG-title fallback.
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
      // The last regex flips group order (episode first, title second).
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

  function extractFromDom() {
    // Last-ditch: pull the most prominent heading and look for an episode
    // number in the page title or near the heading. Loose and false-positive-
    // prone, which is why the floating button always lets the user correct.
    const h1 = document.querySelector('h1')?.textContent?.trim();
    if (!h1) return null;
    const epSource = h1 + ' ' + document.title;
    const m = epSource.match(/Episode\s*(\d+)|Ep\.?\s*(\d+)|\bE(\d+)\b/i);
    if (!m) return null;
    const episode = Number(m[1] ?? m[2] ?? m[3]);
    if (!Number.isFinite(episode) || episode <= 0) return null;
    // Strip the episode marker from the heading to leave just the show.
    const title = h1.replace(/Episode\s*\d+|Ep\.?\s*\d+|\bE\d+\b/i, '').trim();
    return title ? { title, episode, source: 'dom-h1' } : null;
  }

  function detect() {
    return extractFromUrl()
        || extractFromJsonLd()
        || extractFromMeta()
        || extractFromDocumentTitle()
        || extractFromDom();
  }

  // Many anime streaming sites encode { title, episode } directly in the URL
  // path — e.g. /daemons-of-the-shadow-realm-episode-1-english-subbed/. That's
  // often the only reliable signal when the actual <video> lives in a cross-
  // origin iframe and the parent DOM has nothing useful. Cheap and runs first.
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

  // Strips the noise-words streaming sites tack onto titles ("english subbed",
  // "1080p", "raw", …) and title-cases the result.
  function cleanScrapedTitle(s) {
    s = s.replace(/\b(english|sub|subbed|dub|dubbed|raw|hd|fhd|sd|1080p|720p|480p)\b/gi, ' ');
    s = s.replace(/\s+/g, ' ').trim();
    return s.replace(/\b\w/g, c => c.toUpperCase());
  }

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
  `;

  let injected = false;
  let lastDetection = null;
  let autoFired = false;

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

    function paint(detection) {
      $title.value = detection?.title ?? '';
      $episode.value = detection?.episode ?? '';
      $source.textContent = detection?.source
        ? `Detected via ${detection.source}`
        : 'No detection — fill in manually.';
    }
    paint(detection);

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

    return { paint };
  }

  // ── Video watcher ─────────────────────────────────────────────────────
  // Polls for a <video> element (covers SPA navigation that mounts the
  // player after the initial load), then attaches a timeupdate listener
  // that auto-fires once at 80% playback. Many streaming sites embed the
  // player in a cross-origin iframe — there's no <video> in the top frame
  // and we can't reach into the iframe's document. The floating UI still
  // gets injected so the user has a manual save path.

  function watchVideo() {
    const video = document.querySelector('video');
    if (!video) {
      // No video in the top frame yet — could be a SPA still rendering, or
      // a cross-origin iframe player we'll never see. Inject the UI now so
      // the user can save manually on iframed-player sites; keep polling
      // in case a top-frame video shows up later (SPA route change).
      if (!injected && shouldShowUi()) {
        lastDetection = detect();
        injectUi(lastDetection);
      }
      setTimeout(watchVideo, 1500);
      return;
    }

    lastDetection = detect();
    injectUi(lastDetection);

    // Auto-fire only when we had a confident heuristic detection. Pages
    // with no detection require a manual click — the floating button is
    // already injected and pre-filled with whatever we managed to scrape.
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
  }

  // Heuristic for "is this an episode page?" so we don't blanket-inject the
  // Track button on every site. Show the UI when the URL or DOM looks like
  // an episode page (URL slug matches, or any heuristic returned a result).
  function shouldShowUi() {
    if (extractFromUrl()) return true;
    if (extractFromJsonLd()) return true;
    if (extractFromMeta()) return true;
    if (extractFromDocumentTitle()) return true;
    return false;
  }

  watchVideo();
})();
