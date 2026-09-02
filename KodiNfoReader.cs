using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Chronicle.Core.Helpers;
using Chronicle.Plugins.Models;

namespace Chronicle.Plugin.Kodi.NFO;

/// <summary>
/// Everything about READING a Kodi .nfo sidecar: finding it next to a media file, extracting
/// the minimum scan-time matching signal, and the two things Chronicle stores it as (raw
/// lossless capture, and a curated display subset). Ported from Chronicle core's
/// NfoSignalExtractor/NfoDetailParser (Chronicle.Services.Scan) -- moved here per
/// docs/plans/2026-09-02-kodi-nfo-plugin-design.md so Chronicle's own core no longer needs to
/// know Kodi's NFO schema at all. Behavior is unchanged from those two classes; only the
/// location (and, for the curated view, the output shape -- JsonElement instead of a typed
/// NfoDetail record, since the shared plugin interface can't reference a Kodi-specific type)
/// moved.
/// </summary>
internal static class KodiNfoReader
{
    /// <summary>
    /// Kodi's own reserved season/show-level NFO filenames -- never a legitimate per-file
    /// sidecar, so the "any .nfo in folder" fallback below must exclude them. Matching one of
    /// these instead of the true sidecar (e.g. a season NFO's own &lt;title&gt;Season 2&lt;/title&gt;
    /// for every episode in that season folder) would silently overwrite every episode's parsed
    /// title with the season's.
    /// </summary>
    private static readonly Regex SeasonOrShowNfo =
        new(@"^(tvshow|season(-specials|-all)?\d*)\.nfo$",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private const int MaxActors = 20;

    // ── FindSidecar ──────────────────────────────────────────────────────────

    /// <summary>Finds a .nfo sidecar next to <paramref name="mediaFilePath"/>.</summary>
    public static string? FindSidecar(string mediaFilePath)
    {
        var dir  = Path.GetDirectoryName(mediaFilePath);
        var stem = Path.GetFileNameWithoutExtension(mediaFilePath);
        if (dir is null) return null;

        // Prefer "title.nfo" alongside the file
        var adjacent = Path.Combine(dir, stem + ".nfo");
        if (File.Exists(adjacent)) return adjacent;

        // Fall back to any OTHER .nfo in the same folder (e.g. a movie's own NFO named
        // differently from its video file) -- but never a season/show NFO, which describes
        // the whole folder, not this one file.
        try
        {
            return Directory.EnumerateFiles(dir, "*.nfo")
                .FirstOrDefault(f => !SeasonOrShowNfo.IsMatch(Path.GetFileName(f)));
        }
        catch { return null; }
    }

    // ── ExtractSignal (scan-time matching) ──────────────────────────────────

    public static SidecarSignal? ExtractSignal(string sidecarPath)
    {
        if (!File.Exists(sidecarPath)) return null;
        try { return ParseSignalXml(File.ReadAllText(sidecarPath)); }
        catch { return null; }
    }

    /// <summary>Exposed for unit testing against a raw XML string.</summary>
    internal static SidecarSignal? ParseSignalXml(string xml)
    {
        if (string.IsNullOrWhiteSpace(xml)) return null;
        try
        {
            var doc  = XDocument.Parse(xml.Trim());
            var root = doc.Root;
            if (root is null) return null;

            string? Get(string name) =>
                root.Element(name)?.Value?.Trim() is { Length: > 0 } v ? v : null;
            int? GetInt(string name) =>
                int.TryParse(Get(name), out var n) ? n : null;

            // <uniqueid type="tmdb">12345</uniqueid>
            var uid = root.Elements("uniqueid")
                .FirstOrDefault(e => string.Equals(e.Attribute("type")?.Value, "tmdb",
                    StringComparison.OrdinalIgnoreCase));
            var externalId = uid?.Value?.Trim() is { Length: > 0 } id ? id : null;

            return new SidecarSignal(
                Title: Get("title"),
                Year: GetInt("year"),
                Season: GetInt("season"),
                Episode: GetInt("episode"),
                ShowTitle: Get("showtitle"),
                ExternalId: externalId,
                PosterUrl: Get("thumb"),
                Artist: Get("artist"),
                Album: Get("album"));
        }
        catch { return null; }
    }

    // ── CaptureLossless (full lossless capture for storage) ─────────────────

    public static SidecarCapture? CaptureLossless(string sidecarPath)
    {
        if (!File.Exists(sidecarPath)) return null;
        string raw;
        try { raw = File.ReadAllText(sidecarPath); }
        catch { return null; }

        return new SidecarCapture(raw, XmlToJsonConverter.ToJson(raw));
    }

    // ── ExtractCuratedFields (display card) ─────────────────────────────────

    /// <summary>
    /// Curated subset for the media detail page's NFO card. Shaped to match exactly what
    /// Chronicle's frontend NfoDetail TypeScript interface already expects (camelCase keys:
    /// title, originalTitle, plot, genres, rating, mpaa, studio, runtimeMinutes, premiered,
    /// director, writers, actors, collectionName) so moving this out of Chronicle core needed
    /// zero API/frontend changes.
    /// </summary>
    public static JsonElement? ExtractCuratedFields(string sidecarPath)
    {
        if (!File.Exists(sidecarPath)) return null;
        try { return ParseCuratedXml(File.ReadAllText(sidecarPath)); }
        catch { return null; }
    }

    /// <summary>Exposed for unit testing against a raw XML string.</summary>
    internal static JsonElement? ParseCuratedXml(string xml)
    {
        if (string.IsNullOrWhiteSpace(xml)) return null;
        try
        {
            var doc  = XDocument.Parse(xml.Trim());
            var root = doc.Root;
            if (root is null) return null;

            string? Get(string name) =>
                root.Element(name)?.Value?.Trim() is { Length: > 0 } v ? v : null;
            int? GetInt(string name) =>
                int.TryParse(Get(name), out var n) ? n : null;

            var obj = new JsonObject
            {
                ["title"]         = Get("title"),
                ["originalTitle"] = Get("originaltitle"),
                ["plot"]          = Get("plot"),
                ["mpaa"]          = Get("mpaa"),
                ["studio"]        = Get("studio"),
                ["runtimeMinutes"] = GetInt("runtime"),
                // Movie/tvshow NFOs use <premiered> for the release/premiere date; episode
                // NFOs use <aired> instead -- confirmed against Kodi's own NFO schema.
                ["premiered"]     = Get("premiered") ?? Get("aired"),
                ["director"]      = Get("director"),
                ["rating"]        = ParseRating(root),
            };

            var genres = root.Elements("genre")
                .Select(e => e.Value?.Trim())
                .Where(v => !string.IsNullOrEmpty(v))
                .ToList();
            obj["genres"] = new JsonArray(genres.Select(g => (JsonNode)JsonValue.Create(g)!).ToArray());

            var writers = root.Elements("credits")
                .Select(e => e.Value?.Trim())
                .Where(v => !string.IsNullOrEmpty(v))
                .ToList();
            obj["writers"] = new JsonArray(writers.Select(w => (JsonNode)JsonValue.Create(w)!).ToArray());

            obj["collectionName"] = root.Element("set")?.Element("name")?.Value?.Trim() is { Length: > 0 } set
                ? set : null;

            var actors = root.Elements("actor")
                .Select(e => new
                {
                    Name  = e.Element("name")?.Value?.Trim(),
                    Role  = e.Element("role")?.Value?.Trim(),
                    Order = int.TryParse(e.Element("order")?.Value, out var o) ? o : int.MaxValue,
                })
                .Where(x => !string.IsNullOrEmpty(x.Name))
                .OrderBy(x => x.Order)
                .Take(MaxActors)
                .ToList();
            obj["actors"] = new JsonArray(actors.Select(a => (JsonNode)new JsonObject
            {
                ["name"] = a.Name,
                ["role"] = a.Role,
            }).ToArray());

            using var jsonDoc = JsonDocument.Parse(obj.ToJsonString());
            return jsonDoc.RootElement.Clone();
        }
        catch { return null; }
    }

    /// <summary>Prefers the default/imdb rating from &lt;ratings&gt;, falls back to the
    /// legacy flat &lt;rating&gt; element.</summary>
    private static double? ParseRating(XElement root)
    {
        var ratings = root.Element("ratings");
        if (ratings is not null)
        {
            var chosen = ratings.Elements("rating")
                .FirstOrDefault(r => string.Equals(r.Attribute("default")?.Value, "true",
                    StringComparison.OrdinalIgnoreCase))
                ?? ratings.Elements("rating").FirstOrDefault();

            var value = chosen?.Element("value")?.Value;
            if (double.TryParse(value, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var parsed))
                return parsed;
        }

        var flat = root.Element("rating")?.Value;
        if (double.TryParse(flat, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var flatParsed))
            return flatParsed;

        return null;
    }
}
