using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using Chronicle.Plugins.Models;

namespace Chronicle.Plugin.Kodi.NFO;

/// <summary>
/// Builds Kodi-native NFO XML from Chronicle's own resolved data. Ported field-for-field from
/// Chronicle_Scraper's lib/nfo_common.py + lib/nfo_writer.py + lib/tv_nfo_writer.py (movie and
/// TV addons) -- confirmed against that Python source's actual field mapping, not guessed --
/// so what this plugin writes and what the two scraper addons used to write independently are
/// the same document, and the plugin's own reader (KodiNfoReader) can always read it back.
///
/// Deliberately does NOT attempt &lt;fileinfo&gt;&lt;streamdetails&gt; itself -- see
/// SidecarBuildRequest.ExtraFields' own doc for why that specific block can never move
/// server-side (it's Kodi's own file probe, data Chronicle's server has no way to obtain).
/// AddStreamDetailsFromExtra below only ever *splices in* what the caller already supplied.
///
/// Also deliberately does NOT attempt local-art-file fallback discovery (Kodi's own
/// per-slot local file naming convention, e.g. "&lt;video&gt;-poster.jpg") -- explicitly
/// deferred in docs/plans/2026-09-02-kodi-nfo-plugin-design.md as a separate follow-up. Only
/// Chronicle's own remote artwork candidates are written to &lt;art&gt; here.
/// </summary>
internal static class KodiNfoBuilder
{
    private static readonly (string ArtType, string Tag)[] MovieArtTags =
    [
        ("poster", "poster"), ("fanart", "fanart"), ("clearlogo", "clearlogo"),
        ("banner", "banner"), ("clearart", "clearart"), ("discart", "discart"),
        ("characterart", "characterart"),
    ];

    private static readonly string[] UniqueIdTypes = ["imdb", "tmdb", "tvdb", "trakt"];

    public static byte[] Build(SidecarBuildRequest request) => request switch
    {
        MovieSidecarBuildRequest m   => Serialize(BuildMovie(m.Data), m.ExtraFields),
        ShowSidecarBuildRequest s    => Serialize(BuildShow(s.Data), s.ExtraFields),
        EpisodeSidecarBuildRequest e => Serialize(BuildEpisode(e.Data), e.ExtraFields),
        _ => throw new ArgumentException($"Unknown sidecar build request type: {request.GetType()}"),
    };

    // ── Movie ────────────────────────────────────────────────────────────────

    private static XElement BuildMovie(ResolvedMovieData d)
    {
        var root = new XElement("movie");

        AddText(root, "title", d.Title);
        AddText(root, "originaltitle", d.Title);
        AddText(root, "year", d.Year);
        AddText(root, "plot", d.Overview);
        AddText(root, "tagline", d.Tagline);
        if (d.RuntimeMinutes is > 0) AddText(root, "runtime", d.RuntimeMinutes);
        AddText(root, "mpaa", d.Mpaa);
        AddText(root, "premiered", d.Premiered);
        AddText(root, "country", d.Country);
        AddText(root, "studio", d.Studio);

        foreach (var genre in d.Genres ?? []) AddText(root, "genre", genre);
        foreach (var tag in d.Tags ?? []) AddText(root, "tag", tag);

        if (d.Collection is { Name.Length: > 0 } collection)
        {
            var setEl = new XElement("set");
            AddText(setEl, "name", collection.Name);
            AddText(setEl, "overview", collection.Overview);
            root.Add(setEl);
        }

        AddDirectorsAndWriters(root, d.Crew);
        AddActors(root, d.Cast);
        AddUniqueIds(root, d.ExternalIds);
        AddRatings(root, d.Ratings);
        AddArtBlock(root, d.Artwork, MovieArtTags);

        AddText(root, "trailer", d.TrailerUrl);

        return root;
    }

    // ── TV show ──────────────────────────────────────────────────────────────

    private static readonly (string ArtType, string Tag)[] ShowArtTags = MovieArtTags;

    private static XElement BuildShow(ResolvedShowData d)
    {
        var root = new XElement("tvshow");

        AddText(root, "title", d.Title);
        AddText(root, "showtitle", d.Title);
        AddText(root, "year", d.Year);
        AddText(root, "premiered", d.Premiered);
        AddText(root, "plot", d.Overview);
        AddText(root, "mpaa", d.Mpaa);
        AddText(root, "country", d.Country);
        AddText(root, "studio", d.Studio);
        AddText(root, "status", d.Status);
        if (d.RuntimeMinutes is > 0) AddText(root, "runtime", d.RuntimeMinutes);

        foreach (var genre in d.Genres ?? []) AddText(root, "genre", genre);
        foreach (var tag in d.Tags ?? []) AddText(root, "tag", tag);

        // Kodi's <namedseason> tag: only worth writing when Chronicle actually has a real
        // (non-generic) name for the season -- an unnamed "Season N" adds nothing Kodi
        // doesn't already infer from the season's own folder/number.
        foreach (var season in d.Seasons ?? [])
        {
            if (string.IsNullOrWhiteSpace(season.Name)) continue;
            var seasonEl = new XElement("namedseason", new XAttribute("number", season.Number));
            seasonEl.Value = season.Name;
            root.Add(seasonEl);
        }

        AddActors(root, d.Cast);
        AddUniqueIds(root, d.ExternalIds);
        AddRatings(root, d.Ratings);
        AddArtBlock(root, d.Artwork, ShowArtTags);

        AddText(root, "trailer", d.TrailerUrl);

        return root;
    }

    // ── Episode ──────────────────────────────────────────────────────────────

    private static XElement BuildEpisode(ResolvedEpisodeData d)
    {
        var root = new XElement("episodedetails");

        AddText(root, "title", d.Title);
        AddText(root, "season", d.Season);
        AddText(root, "episode", d.Episode);
        AddText(root, "plot", d.Overview);
        AddText(root, "aired", d.Aired);
        if (d.RuntimeMinutes is > 0) AddText(root, "runtime", d.RuntimeMinutes);

        AddDirectorsAndWriters(root, d.Crew);
        AddActors(root, d.Cast);
        AddUniqueIds(root, d.ExternalIds);
        AddRatings(root, d.Ratings);

        // Episodes have a single <thumb> (still/screenshot), not the full <art> block --
        // confirmed live against a real Kodi 21+ episode: they carry their own 'thumb', not
        // poster/fanart (those only exist inherited from the show).
        if (!string.IsNullOrEmpty(d.ThumbUrl))
        {
            var artEl = new XElement("art");
            AddText(artEl, "thumb", d.ThumbUrl);
            root.Add(artEl);
        }

        return root;
    }

    // ── Shared building blocks (ported from nfo_common.py) ──────────────────

    private static void AddText(XElement parent, string tag, object? value)
    {
        // IFormattable+InvariantCulture for numbers -- ToString() alone follows the host's
        // current culture, which for a double like a rating would write "6,5" instead of
        // "6.5" on a non-en-US locale and silently corrupt the value for any XML parser
        // (Kodi included) expecting an invariant decimal point.
        var text = value switch
        {
            null => null,
            string s => string.IsNullOrEmpty(s) ? null : s,
            IFormattable f => f.ToString(null, System.Globalization.CultureInfo.InvariantCulture),
            _ => value.ToString(),
        };
        if (text is null) return;
        parent.Add(new XElement(tag, text));
    }

    private static void AddActors(XElement root, List<CastMember>? cast)
    {
        var order = 0;
        foreach (var actor in cast ?? [])
        {
            if (string.IsNullOrWhiteSpace(actor.Name)) continue;
            var actorEl = new XElement("actor");
            AddText(actorEl, "name", actor.Name);
            AddText(actorEl, "role", actor.Role);
            // Chronicle's own resolved headshot for this person, when known -- Kodi's actor
            // schema already supports <thumb>, this just gives it something to hold.
            AddText(actorEl, "thumb", actor.ProfileImageUrl);
            AddText(actorEl, "order", order++);
            root.Add(actorEl);
        }
    }

    /// <summary>Kodi's NFO only has dedicated person-tags for director and writer
    /// ("credits") -- every other job title (producer, composer, etc.) has no NFO-equivalent
    /// tag, so it isn't written even though Chronicle keeps every crew credit it was given.</summary>
    private static void AddDirectorsAndWriters(XElement root, List<CrewMember>? crew)
    {
        foreach (var member in crew ?? [])
        {
            var job = (member.Job ?? string.Empty).ToLowerInvariant();
            if (job == "director")
                AddText(root, "director", member.Name);
            else if (job is "writer" or "screenplay" or "story" or "teleplay")
                AddText(root, "credits", member.Name);
        }
    }

    private static void AddUniqueIds(XElement root, ResolvedExternalIds? externalIds)
    {
        if (externalIds is null) return;

        void AddOne(string type, string? value)
        {
            if (string.IsNullOrEmpty(value)) return;
            var el = new XElement("uniqueid", new XAttribute("type", type), value);
            if (type == "imdb") el.Add(new XAttribute("default", "true"));
            root.Add(el);
        }

        foreach (var type in UniqueIdTypes)
        {
            var value = type switch
            {
                "imdb" => externalIds.Imdb,
                "tmdb" => externalIds.Tmdb,
                "tvdb" => externalIds.Tvdb,
                "trakt" => externalIds.Trakt,
                _ => null,
            };
            AddOne(type, value);
        }
    }

    private static void AddRatings(XElement root, Dictionary<string, ResolvedRating>? ratings)
    {
        if (ratings is not { Count: > 0 }) return;

        var ratingsEl = new XElement("ratings");
        foreach (var (source, rating) in ratings)
        {
            var ratingEl = new XElement("rating",
                new XAttribute("name", source), new XAttribute("max", "10"));
            AddText(ratingEl, "value", rating.Rating);
            AddText(ratingEl, "votes", rating.Votes ?? 0);
            ratingsEl.Add(ratingEl);
        }
        root.Add(ratingsEl);
    }

    private static void AddArtBlock(
        XElement root, Dictionary<string, List<ResolvedArtworkCandidate>>? artwork,
        (string ArtType, string Tag)[] artTags)
    {
        if (artwork is not { Count: > 0 }) return;

        var artEl = new XElement("art");
        foreach (var (artType, tag) in artTags)
        {
            if (!artwork.TryGetValue(artType, out var candidates) || candidates is not { Count: > 0 })
                continue;

            // Kodi's own "Choose Art" dialog only ever offers alternates for a slot when the
            // NFO itself lists more than one -- a bare "<poster>url</poster>" gives Kodi
            // nothing to pick between. <fanart> has always used the wrapped
            // "<fanart><thumb>url</thumb>...</fanart>" form (even for a single candidate), so
            // Kodi's fanart picker already sees every image Chronicle knows about. Every other
            // slot used to write ONLY its single top candidate as a bare tag -- Chronicle's own
            // resolved pick -- so Kodi's poster/clearlogo/banner/etc. picker only ever had that
            // one option, with no way to browse Chronicle's other candidates from inside Kodi
            // at all. Confirmed live (2026-09-12): switching posters inside Kodi for "Spider-Man:
            // Brand New Day" offered nothing but a blank slot and an auto-generated video still,
            // never any of the real posters Chronicle already had resolved for it.
            //
            // Any slot with more than one candidate now gets the same wrapped form as fanart,
            // so Kodi's picker for every slot can offer every image Chronicle has -- not just
            // the one it already chose. A single-candidate slot keeps writing the bare tag
            // (matches Kodi's own scraper output for that case, and there's no alternate to
            // offer anyway).
            if (artType == "fanart" || candidates.Count > 1)
            {
                var containerEl = new XElement(artType == "fanart" ? "fanart" : tag);
                foreach (var candidate in candidates)
                    AddText(containerEl, "thumb", candidate.Url);
                artEl.Add(containerEl);
            }
            else
            {
                AddText(artEl, tag, candidates[0].Url);
            }
        }

        if (artEl.HasElements) root.Add(artEl);
    }

    /// <summary>
    /// Splices &lt;fileinfo&gt;&lt;streamdetails&gt; in from the caller's own supplied data
    /// (see SidecarBuildRequest.ExtraFields), if present, under the key "streamdetails". The
    /// expected shape mirrors Kodi's own VideoLibrary.GetMovies/GetEpisodes streamdetails
    /// property exactly: { "video": [{codec, aspect, width, height, durationInSeconds,
    /// stereoMode, hdrType}], "audio": [{codec, language, channels}], "subtitle": [{language}] }.
    /// Silently does nothing if absent or malformed -- this is optional, best-effort data the
    /// caller may not have yet (e.g. a brand-new file Kodi hasn't probed).
    /// </summary>
    private static void AddStreamDetailsFromExtra(
        XElement root, IReadOnlyDictionary<string, JsonElement>? extraFields)
    {
        if (extraFields is null || !extraFields.TryGetValue("streamdetails", out var sd)) return;
        if (sd.ValueKind != JsonValueKind.Object) return;

        var fileinfoEl = new XElement("fileinfo");
        var sdEl = new XElement("streamdetails");

        if (sd.TryGetProperty("video", out var videoArr) && videoArr.ValueKind == JsonValueKind.Array)
        {
            foreach (var v in videoArr.EnumerateArray())
            {
                var vEl = new XElement("video");
                AddTextFromJson(vEl, "codec", v, "codec");
                AddTextFromJson(vEl, "aspect", v, "aspect");
                AddTextFromJson(vEl, "width", v, "width");
                AddTextFromJson(vEl, "height", v, "height");
                AddTextFromJson(vEl, "durationinseconds", v, "durationInSeconds");
                AddTextFromJson(vEl, "stereomode", v, "stereoMode");
                AddTextFromJson(vEl, "hdrtype", v, "hdrType");
                sdEl.Add(vEl);
            }
        }

        if (sd.TryGetProperty("audio", out var audioArr) && audioArr.ValueKind == JsonValueKind.Array)
        {
            foreach (var a in audioArr.EnumerateArray())
            {
                var aEl = new XElement("audio");
                AddTextFromJson(aEl, "codec", a, "codec");
                AddTextFromJson(aEl, "language", a, "language");
                AddTextFromJson(aEl, "channels", a, "channels");
                sdEl.Add(aEl);
            }
        }

        if (sd.TryGetProperty("subtitle", out var subArr) && subArr.ValueKind == JsonValueKind.Array)
        {
            foreach (var s in subArr.EnumerateArray())
            {
                var sEl = new XElement("subtitle");
                AddTextFromJson(sEl, "language", s, "language");
                sdEl.Add(sEl);
            }
        }

        if (!sdEl.HasElements) return;
        fileinfoEl.Add(sdEl);
        root.Add(fileinfoEl);
    }

    private static void AddTextFromJson(XElement parent, string tag, JsonElement obj, string propertyName)
    {
        if (!obj.TryGetProperty(propertyName, out var prop) || prop.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            return;
        var text = prop.ValueKind switch
        {
            JsonValueKind.String => prop.GetString(),
            JsonValueKind.Number => prop.GetRawText(),
            JsonValueKind.True or JsonValueKind.False => prop.GetRawText(),
            _ => null,
        };
        if (string.IsNullOrEmpty(text)) return;
        parent.Add(new XElement(tag, text));
    }

    // ── Serialization ────────────────────────────────────────────────────────

    private static byte[] Serialize(XElement root, IReadOnlyDictionary<string, JsonElement>? extraFields)
    {
        AddStreamDetailsFromExtra(root, extraFields);

        var declaration = new XDeclaration("1.0", "UTF-8", "yes");
        var doc = new XDocument(declaration, root);

        using var ms = new MemoryStream();
        // XDocument.Save writes its own declaration via an XmlWriter with the right settings;
        // matching Chronicle_Scraper's own output byte-for-byte (UTF-8, no BOM) rather than
        // relying on defaults.
        using (var writer = System.Xml.XmlWriter.Create(ms, new System.Xml.XmlWriterSettings
        {
            Encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            Indent = false,
            OmitXmlDeclaration = false,
        }))
        {
            doc.Save(writer);
        }
        return ms.ToArray();
    }
}
