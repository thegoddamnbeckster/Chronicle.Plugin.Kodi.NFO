using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using Chronicle.Plugin.Kodi.NFO;
using Chronicle.Plugins.Models;
using FluentAssertions;
using Xunit;

namespace Chronicle.Plugin.Kodi.NFO.Tests;

public class KodiNfoBuilderTests
{
    private static XElement Parse(byte[] bytes) =>
        XDocument.Parse(Encoding.UTF8.GetString(bytes)).Root!;

    [Fact]
    public void Build_Movie_WritesCoreFields()
    {
        var data = new ResolvedMovieData(
            Title: "2 Fast 2 Furious",
            Overview: "It's a major double-cross...",
            Tagline: "Tune up. Rev up. Buckle up.",
            Year: 2003,
            Premiered: "2003-06-05",
            Mpaa: "14A",
            Country: "USA",
            Studio: "Ardustry Entertainment",
            RuntimeMinutes: 108,
            Genres: ["Action", "Crime"],
            Cast: [new CastMember("Paul Walker", "Brian O'Conner")],
            Crew: [new CrewMember("John Singleton", "Director")],
            Tags: null,
            Ratings: new Dictionary<string, ResolvedRating> { ["imdb"] = new(6.0, 325462) },
            TrailerUrl: "https://example.com/trailer",
            ExternalIds: new ResolvedExternalIds("tt0322259", null, "584", null),
            Artwork: new Dictionary<string, List<ResolvedArtworkCandidate>>
            {
                ["poster"] = [new ResolvedArtworkCandidate("https://example.com/poster.jpg", "tmdb")],
            },
            Collection: new ResolvedCollection("The Fast and the Furious Collection"));

        var root = Parse(KodiNfoBuilder.Build(new MovieSidecarBuildRequest(data)));

        root.Name.LocalName.Should().Be("movie");
        root.Element("title")!.Value.Should().Be("2 Fast 2 Furious");
        root.Element("originaltitle")!.Value.Should().Be("2 Fast 2 Furious");
        root.Element("plot")!.Value.Should().Be(data.Overview);
        root.Element("runtime")!.Value.Should().Be("108");
        root.Element("premiered")!.Value.Should().Be("2003-06-05");
        root.Elements("genre").Select(e => e.Value).Should().Equal("Action", "Crime");
        root.Element("director")!.Value.Should().Be("John Singleton");
        root.Element("set")!.Element("name")!.Value.Should().Be("The Fast and the Furious Collection");

        var actor = root.Element("actor")!;
        actor.Element("name")!.Value.Should().Be("Paul Walker");
        actor.Element("role")!.Value.Should().Be("Brian O'Conner");

        var uniqueIds = root.Elements("uniqueid").ToList();
        uniqueIds.Should().Contain(e => e.Attribute("type")!.Value == "imdb" && e.Value == "tt0322259");
        uniqueIds.Should().Contain(e => e.Attribute("type")!.Value == "tmdb" && e.Value == "584");
        uniqueIds.First(e => e.Attribute("type")!.Value == "imdb").Attribute("default")!.Value.Should().Be("true");

        var rating = root.Element("ratings")!.Element("rating")!;
        rating.Attribute("name")!.Value.Should().Be("imdb");
        rating.Element("value")!.Value.Should().Be("6");
        rating.Element("votes")!.Value.Should().Be("325462");

        root.Element("art")!.Element("poster")!.Value.Should().Be("https://example.com/poster.jpg");
        root.Element("trailer")!.Value.Should().Be("https://example.com/trailer");
    }

    [Fact]
    public void Build_Movie_OnlyDirectorAndWriterCrewJobsAreWritten()
    {
        var data = MinimalMovie() with
        {
            Crew =
            [
                new CrewMember("Director Name", "Director"),
                new CrewMember("Writer Name", "Screenplay"),
                new CrewMember("Composer Name", "Composer"), // no NFO tag for this -- must be dropped
            ],
        };

        var root = Parse(KodiNfoBuilder.Build(new MovieSidecarBuildRequest(data)));

        root.Element("director")!.Value.Should().Be("Director Name");
        root.Element("credits")!.Value.Should().Be("Writer Name");
        root.Elements().Select(e => e.Value).Should().NotContain("Composer Name");
    }

    [Fact]
    public void Build_Episode_UsesAiredNotPremiered()
    {
        var data = new ResolvedEpisodeData(
            Title: "Pilot", Overview: "...", Season: 1, Episode: 1, Year: 2008,
            Aired: "2008-01-20", RuntimeMinutes: 58, Cast: null, Crew: null, Ratings: null,
            ThumbUrl: "https://example.com/thumb.jpg", ExternalIds: null,
            ShowTitle: "Breaking Bad", ShowYear: 2008);

        var root = Parse(KodiNfoBuilder.Build(new EpisodeSidecarBuildRequest(data)));

        root.Name.LocalName.Should().Be("episodedetails");
        root.Element("aired")!.Value.Should().Be("2008-01-20");
        root.Element("premiered").Should().BeNull(); // episodes never use <premiered>
        root.Element("art")!.Element("thumb")!.Value.Should().Be("https://example.com/thumb.jpg");
    }

    [Fact]
    public void Build_Show_UsesNamedSeasonOnlyWhenNamed()
    {
        var data = new ResolvedShowData(
            Title: "Breaking Bad", Overview: null, Tagline: null, Year: 2008, Premiered: "2008-01-20",
            Mpaa: null, Country: null, Studio: null, Status: "Ended", RuntimeMinutes: null,
            Genres: null, Cast: null, Crew: null, Tags: null, Ratings: null, TrailerUrl: null,
            ExternalIds: null, Artwork: null,
            Seasons: [new ResolvedSeason(0, "Specials", null), new ResolvedSeason(1, null, null)]);

        var root = Parse(KodiNfoBuilder.Build(new ShowSidecarBuildRequest(data)));

        root.Name.LocalName.Should().Be("tvshow");
        root.Element("showtitle")!.Value.Should().Be("Breaking Bad");
        root.Element("status")!.Value.Should().Be("Ended");
        var namedSeasons = root.Elements("namedseason").ToList();
        namedSeasons.Should().ContainSingle(); // only season 0 has a real name
        namedSeasons[0].Attribute("number")!.Value.Should().Be("0");
        namedSeasons[0].Value.Should().Be("Specials");
    }

    [Fact]
    public void Build_ExtraFieldsStreamDetails_IsSplicedIn()
    {
        var streamDetailsJson = JsonDocument.Parse("""
            {
              "video": [{"codec": "hevc", "width": 3840, "height": 2160, "hdrType": "dolbyvision"}],
              "audio": [{"codec": "truehd", "channels": 8, "language": "eng"}],
              "subtitle": [{"language": "eng"}]
            }
            """).RootElement;

        var extra = new Dictionary<string, JsonElement> { ["streamdetails"] = streamDetailsJson };
        var request = new MovieSidecarBuildRequest(MinimalMovie()) { ExtraFields = extra };

        var root = Parse(KodiNfoBuilder.Build(request));

        var sd = root.Element("fileinfo")!.Element("streamdetails")!;
        var video = sd.Element("video")!;
        video.Element("codec")!.Value.Should().Be("hevc");
        video.Element("width")!.Value.Should().Be("3840");
        video.Element("hdrtype")!.Value.Should().Be("dolbyvision");

        var audio = sd.Element("audio")!;
        audio.Element("codec")!.Value.Should().Be("truehd");
        audio.Element("channels")!.Value.Should().Be("8");

        sd.Element("subtitle")!.Element("language")!.Value.Should().Be("eng");
    }

    [Fact]
    public void Build_NoExtraFields_NoFileinfoElementAtAll()
    {
        var root = Parse(KodiNfoBuilder.Build(new MovieSidecarBuildRequest(MinimalMovie())));

        root.Element("fileinfo").Should().BeNull();
    }

    [Fact]
    public void Build_RoundTripsThroughKodiNfoReader()
    {
        // The whole point of one interface for both directions: what this builds, the reader
        // must be able to read back correctly.
        var data = MinimalMovie() with { Title = "Round Trip Movie", Premiered = "2020-05-01" };
        var bytes = KodiNfoBuilder.Build(new MovieSidecarBuildRequest(data));
        var xml = Encoding.UTF8.GetString(bytes);

        var signal = KodiNfoReader.ParseSignalXml(xml);
        signal!.Title.Should().Be("Round Trip Movie");

        var curated = KodiNfoReader.ParseCuratedXml(xml);
        curated!.Value.GetProperty("premiered").GetString().Should().Be("2020-05-01");
    }

    [Fact]
    public void Build_SinglePosterCandidate_WritesABareTag()
    {
        var data = MinimalMovie() with
        {
            Artwork = new Dictionary<string, List<ResolvedArtworkCandidate>>
            {
                ["poster"] = [new ResolvedArtworkCandidate("https://example.com/poster.jpg", "tmdb")],
            },
        };

        var root = Parse(KodiNfoBuilder.Build(new MovieSidecarBuildRequest(data)));

        var posterEl = root.Element("art")!.Element("poster")!;
        posterEl.HasElements.Should().BeFalse();
        posterEl.Value.Should().Be("https://example.com/poster.jpg");
    }

    [Fact]
    public void Build_MultiplePosterCandidates_WrapsThemAllAsThumbsLikeFanart()
    {
        // Root-caused live (2026-09-12): before this, only the single top poster candidate
        // ever reached the NFO, so Kodi's own "Choose Art" picker had nothing else to offer --
        // confirmed on "Spider-Man: Brand New Day", whose picker showed only a blank slot and
        // an auto-generated video still despite Chronicle already having real posters resolved.
        var data = MinimalMovie() with
        {
            Artwork = new Dictionary<string, List<ResolvedArtworkCandidate>>
            {
                ["poster"] =
                [
                    new ResolvedArtworkCandidate("https://example.com/poster1.jpg", "tmdb"),
                    new ResolvedArtworkCandidate("https://example.com/poster2.jpg", "fanarttv"),
                ],
            },
        };

        var root = Parse(KodiNfoBuilder.Build(new MovieSidecarBuildRequest(data)));

        var posterEl = root.Element("art")!.Element("poster")!;
        posterEl.Elements("thumb").Select(e => e.Value).Should()
            .Equal("https://example.com/poster1.jpg", "https://example.com/poster2.jpg");
    }

    [Fact]
    public void Build_MultipleClearlogoCandidates_AlsoWrapsAsThumbs()
    {
        // Confirms the multi-candidate wrapping isn't special-cased to poster -- every
        // single-value art slot (clearlogo, banner, clearart, discart, characterart) gets the
        // same treatment when Chronicle has more than one candidate for it.
        var data = MinimalMovie() with
        {
            Artwork = new Dictionary<string, List<ResolvedArtworkCandidate>>
            {
                ["clearlogo"] =
                [
                    new ResolvedArtworkCandidate("https://example.com/logo1.png", "fanarttv"),
                    new ResolvedArtworkCandidate("https://example.com/logo2.png", "fanarttv"),
                ],
            },
        };

        var root = Parse(KodiNfoBuilder.Build(new MovieSidecarBuildRequest(data)));

        var clearlogoEl = root.Element("art")!.Element("clearlogo")!;
        clearlogoEl.Elements("thumb").Select(e => e.Value).Should()
            .Equal("https://example.com/logo1.png", "https://example.com/logo2.png");
    }

    private static ResolvedMovieData MinimalMovie() => new(
        Title: "Minimal Movie", Overview: null, Tagline: null, Year: 2020, Premiered: null,
        Mpaa: null, Country: null, Studio: null, RuntimeMinutes: null, Genres: null, Cast: null,
        Crew: null, Tags: null, Ratings: null, TrailerUrl: null, ExternalIds: null, Artwork: null,
        Collection: null);
}
