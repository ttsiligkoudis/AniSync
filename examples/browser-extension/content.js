// AniSync auto-tracker content script.
//
// Runs on streaming-site episode pages, finds the <video> element, watches its
// timeupdate events, and once the user crosses the "watched" threshold sends
// the show title + episode number to the background worker. The background
// worker handles the actual API calls so this script doesn't need permissions
// for the AniSync host.
//
// The 80% completion threshold matches the convention every major tracker
// uses. An episode that's only partially watched stays unmarked so flipping
// to a different tab during an OP doesn't fire false-positive saves.

(function () {
  'use strict';

  // Per-site title + episode extractors. Streaming sites change DOM layout
  // frequently, so each function tries multiple fallbacks and returns null
  // when nothing reliable is found — better silent than wrong.
  const SITES = {
    'crunchyroll.com': () => {
      const ld = document.querySelector('script[type="application/ld+json"]');
      if (ld) {
        try {
          const data = JSON.parse(ld.textContent);
          if (data.partOfSeries && data.episodeNumber) {
            return { title: data.partOfSeries.name, episode: Number(data.episodeNumber) };
          }
        } catch { /* fall through to DOM */ }
      }
      const series = document.querySelector('a[href*="/series/"]')?.textContent?.trim();
      const epText = document.querySelector('[data-t="title"]')?.textContent?.trim() ?? '';
      const epMatch = epText.match(/E(\d+)/i) ?? document.title.match(/Episode\s+(\d+)/i);
      return series && epMatch ? { title: series, episode: Number(epMatch[1]) } : null;
    },
    'hidive.com': () => {
      const series = document.querySelector('h1, [data-testid="show-title"]')?.textContent?.trim();
      const m = document.title.match(/Episode\s+(\d+)/i);
      return series && m ? { title: series, episode: Number(m[1]) } : null;
    },
    'netflix.com': () => {
      // Netflix doesn't expose anime-style "episode N of show" cleanly — the
      // user has to rely on the manual flow for non-anime-first sites. Return
      // null so we don't fire bogus updates.
      return null;
    },
  };

  function detect() {
    const host = location.hostname.replace(/^www\./, '');
    for (const [domain, extract] of Object.entries(SITES)) {
      if (host.endsWith(domain)) return extract();
    }
    return null;
  }

  function watchVideo() {
    const video = document.querySelector('video');
    if (!video) {
      // The player can mount after the script runs — retry shortly.
      setTimeout(watchVideo, 1500);
      return;
    }

    const meta = detect();
    if (!meta) return;

    let fired = false;
    video.addEventListener('timeupdate', () => {
      if (fired || !video.duration) return;
      const pct = video.currentTime / video.duration;
      if (pct < 0.8) return;
      fired = true;

      // Hand off to the background worker. The content script never sees the
      // API URL or UID — those live in chrome.storage.local and are read by
      // background.js, so a compromised page can't extract them from this
      // execution context.
      chrome.runtime.sendMessage({
        type: 'anisync:watched',
        title: meta.title,
        episode: meta.episode,
        source: location.hostname,
      });
    });
  }

  watchVideo();
})();
