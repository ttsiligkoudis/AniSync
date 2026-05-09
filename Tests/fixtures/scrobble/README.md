# Scrobble webhook smoke tests

End-to-end smoke recipes for the `/api/v1/scrobble/{token}` endpoint. Run these against a
locally running AniSync where you've copied your scrobble token from the configure page
(Home Server Sync → Webhook URL → the last path segment).

The Frieren IDs in these fixtures are real — IMDB tt22248376, TVDB 422598, TMDB 209867. As
long as your linked tracker has Frieren in its catalog, the dispatch should succeed.

## Plex (multipart/form-data)

```bash
TOKEN="paste-from-configure-page"
curl -i -X POST "http://localhost:5000/api/v1/scrobble/$TOKEN" \
    -F "payload=<plex.json"
```

## Jellyfin (application/json)

```bash
TOKEN="paste-from-configure-page"
curl -i -X POST "http://localhost:5000/api/v1/scrobble/$TOKEN" \
    -H 'Content-Type: application/json' \
    --data-binary @jellyfin.json
```

## Emby (application/json)

```bash
TOKEN="paste-from-configure-page"
curl -i -X POST "http://localhost:5000/api/v1/scrobble/$TOKEN" \
    -H 'Content-Type: application/json' \
    --data-binary @emby.json
```

## What to look for

- `200 OK` on every request (the endpoint always 200s — even on bad tokens we 401 only
  for an explicitly empty/missing token, never on a parse failure or a missed mapping).
- A log line of the shape `Scrobble plex|jellyfin|emby uid=… anime=… S1E1.` — that's the
  successful path.
- A log line of the shape `Scrobble dropped — no mapping for Frieren S1E1` means the
  external-id resolver didn't find the show in the bundled mapping data. Try a different
  fixture or wait for the next 24h refresh of the mapping cache.
- Hitting any of the three twice within 60s should produce a `Scrobble dropped (dedup)`
  line on the second one — that's the in-memory dedup window working.
