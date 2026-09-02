using System.Text.Json;
using Chronicle.Plugin.Kodi.NFO;
using FluentAssertions;
using Xunit;

namespace Chronicle.Plugin.Kodi.NFO.Tests;

public class KodiNfoReaderTests
{
    // ── FindSidecar ──────────────────────────────────────────────────────────

    [Fact]
    public void FindSidecar_PrefersStemMatch()
    {
        var dir = Directory.CreateTempSubdirectory("kodi_nfo_reader_test_");
        try
        {
            var videoPath = Path.Combine(dir.FullName, "Movie (2020).mkv");
            var stemNfo = Path.Combine(dir.FullName, "Movie (2020).nfo");
            var otherNfo = Path.Combine(dir.FullName, "other.nfo");
            File.WriteAllText(videoPath, "");
            File.WriteAllText(otherNfo, "<movie><title>Other</title></movie>");
            File.WriteAllText(stemNfo, "<movie><title>Stem</title></movie>");

            KodiNfoReader.FindSidecar(videoPath).Should().Be(stemNfo);
        }
        finally { dir.Delete(recursive: true); }
    }

    [Fact]
    public void FindSidecar_ExcludesShowAndSeasonNfos()
    {
        var dir = Directory.CreateTempSubdirectory("kodi_nfo_reader_test_");
        try
        {
            var videoPath = Path.Combine(dir.FullName, "S01E01.mkv");
            File.WriteAllText(videoPath, "");
            File.WriteAllText(Path.Combine(dir.FullName, "tvshow.nfo"), "<tvshow><title>Show</title></tvshow>");

            KodiNfoReader.FindSidecar(videoPath).Should().BeNull();
        }
        finally { dir.Delete(recursive: true); }
    }

    [Fact]
    public void FindSidecar_NoNfoAtAll_ReturnsNull()
    {
        var dir = Directory.CreateTempSubdirectory("kodi_nfo_reader_test_");
        try
        {
            var videoPath = Path.Combine(dir.FullName, "Movie.mkv");
            File.WriteAllText(videoPath, "");
            KodiNfoReader.FindSidecar(videoPath).Should().BeNull();
        }
        finally { dir.Delete(recursive: true); }
    }

    // ── ExtractSignal ────────────────────────────────────────────────────────

    [Fact]
    public void ParseSignalXml_Episode_ExtractsShowTitleSeasonEpisode()
    {
        const string xml = """
            <episodedetails>
              <title>Pilot</title>
              <showtitle>Breaking Bad</showtitle>
              <season>1</season>
              <episode>1</episode>
              <uniqueid type="tmdb">62085</uniqueid>
            </episodedetails>
            """;

        var signal = KodiNfoReader.ParseSignalXml(xml);

        signal.Should().NotBeNull();
        signal!.Title.Should().Be("Pilot");
        signal.ShowTitle.Should().Be("Breaking Bad");
        signal.Season.Should().Be(1);
        signal.Episode.Should().Be(1);
        signal.ExternalId.Should().Be("62085");
    }

    [Fact]
    public void ParseSignalXml_MalformedXml_ReturnsNull()
    {
        KodiNfoReader.ParseSignalXml("<movie><title>Unclosed").Should().BeNull();
    }

    [Fact]
    public void ParseSignalXml_MusicNfo_ExtractsArtistAndAlbum()
    {
        // ScanGroupingService's own level-0 (Artist)/level-1 (Album) grouping reads exactly
        // these two fields -- dropped in an earlier draft of this port and caught before
        // Chronicle's core was wired to depend on this plugin.
        const string xml = "<album><title>The Black Album</title><artist>Metallica</artist><album>The Black Album</album></album>";

        var signal = KodiNfoReader.ParseSignalXml(xml);

        signal!.Artist.Should().Be("Metallica");
        signal.Album.Should().Be("The Black Album");
    }

    // ── CaptureLossless ──────────────────────────────────────────────────────

    [Fact]
    public void CaptureLossless_RealFile_CapturesRawAndParsed()
    {
        var dir = Directory.CreateTempSubdirectory("kodi_nfo_reader_test_");
        try
        {
            var nfoPath = Path.Combine(dir.FullName, "movie.nfo");
            const string xml = "<movie><title>2 Fast 2 Furious</title></movie>";
            File.WriteAllText(nfoPath, xml);

            var capture = KodiNfoReader.CaptureLossless(nfoPath);

            capture.Should().NotBeNull();
            capture!.RawText.Should().Be(xml);
            capture.Parsed!.Value.GetProperty("title").GetString().Should().Be("2 Fast 2 Furious");
        }
        finally { dir.Delete(recursive: true); }
    }

    [Fact]
    public void CaptureLossless_MissingFile_ReturnsNull()
    {
        KodiNfoReader.CaptureLossless(@"/does/not/exist.nfo").Should().BeNull();
    }

    // ── ExtractCuratedFields ─────────────────────────────────────────────────

    [Fact]
    public void ParseCuratedXml_FullNfo_MatchesLegacyNfoDetailShape()
    {
        const string xml = """
            <movie>
              <title>2 Fast 2 Furious</title>
              <originaltitle>2 Fast 2 Furious</originaltitle>
              <ratings>
                <rating name="imdb" max="10" default="true">
                  <value>6.0</value>
                  <votes>325462</votes>
                </rating>
              </ratings>
              <plot>It's a major double-cross...</plot>
              <runtime>108</runtime>
              <mpaa>14A</mpaa>
              <genre>Action</genre>
              <genre>Crime</genre>
              <set><name>The Fast and the Furious Collection</name></set>
              <credits>Michael Brandt</credits>
              <director>John Singleton</director>
              <premiered>2003-06-05</premiered>
              <studio>Ardustry Entertainment</studio>
              <actor><name>Paul Walker</name><role>Brian O'Conner</role><order>0</order></actor>
            </movie>
            """;

        var json = KodiNfoReader.ParseCuratedXml(xml);

        json.Should().NotBeNull();
        var v = json!.Value;
        v.GetProperty("title").GetString().Should().Be("2 Fast 2 Furious");
        v.GetProperty("plot").GetString().Should().Be("It's a major double-cross...");
        v.GetProperty("rating").GetDouble().Should().Be(6.0);
        v.GetProperty("runtimeMinutes").GetInt32().Should().Be(108);
        v.GetProperty("premiered").GetString().Should().Be("2003-06-05");
        v.GetProperty("director").GetString().Should().Be("John Singleton");
        v.GetProperty("collectionName").GetString().Should().Be("The Fast and the Furious Collection");
        v.GetProperty("genres").EnumerateArray().Select(e => e.GetString()).Should().Equal("Action", "Crime");
        v.GetProperty("writers").EnumerateArray().Select(e => e.GetString()).Should().Equal("Michael Brandt");
        var actors = v.GetProperty("actors").EnumerateArray().ToList();
        actors.Should().ContainSingle();
        actors[0].GetProperty("name").GetString().Should().Be("Paul Walker");
        actors[0].GetProperty("role").GetString().Should().Be("Brian O'Conner");
    }

    [Fact]
    public void ParseCuratedXml_EpisodeNfo_FallsBackToAiredForPremiered()
    {
        const string xml = """
            <episodedetails>
              <title>Pilot</title>
              <aired>2008-01-20</aired>
            </episodedetails>
            """;

        var json = KodiNfoReader.ParseCuratedXml(xml);

        json!.Value.GetProperty("premiered").GetString().Should().Be("2008-01-20");
    }
}
