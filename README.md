# Chronicle.Plugin.Kodi.NFO

Reads and writes Kodi's `.nfo` sidecar format for [Chronicle](https://github.com/thegoddamnbeckster/Chronicle) — one plugin, both directions.

See `docs/plans/2026-09-02-kodi-nfo-plugin-design.md` in the main Chronicle repo for the full design and why this exists: Kodi's NFO schema was previously hardcoded into Chronicle's own core (`Chronicle.Services.Scan`), violating Chronicle's plugin-first architecture, and the same field-mapping logic was independently duplicated in Python across both `Chronicle_Scraper` Kodi addons (movie and TV). This plugin is the single place that knowledge now lives.

## What it does

**Read side** (during a Chronicle file scan):
- Finds the `.nfo` sidecar belonging to a scanned media file (Kodi's own "prefer `<video-stem>.nfo`, never a `tvshow.nfo`/`season*.nfo`" convention).
- Extracts the minimum signal Chronicle's scan-time matching needs (title, year, season/episode, a primary TMDB id).
- Captures the sidecar losslessly for storage — raw text plus a fully generic structured parse (every element and attribute, not a curated field list).
- Extracts a curated display subset (plot, genres, rating, cast, etc.) for Chronicle's media detail page.

**Write side** (on demand, via a Chronicle API endpoint the Kodi scraper addons call):
- Builds Kodi-native `movie.nfo` / `tvshow.nfo` / `episodedetails.nfo` XML from Chronicle's own resolved item data — title, plot, ratings, cast, crew, `<uniqueid>`s, artwork, collection info, and more.
- Never touches `<fileinfo><streamdetails>` (codec, resolution, HDR, channels) itself — that's Kodi's own file-probe data, which Chronicle's server has no way to obtain. The caller supplies it via `SidecarBuildRequest.ExtraFields["streamdetails"]` and this plugin splices it in verbatim.
- Does not attempt local-art-file fallback discovery (Kodi's own per-slot local naming convention) — deferred, see the design doc.

## Supported Media Types

`movies`, `tv`, `fanedits` — this plugin never introduces a new media type of its own; it only reads/writes sidecars for types that already exist in Chronicle.

## Settings

None. No credentials or configuration needed.
