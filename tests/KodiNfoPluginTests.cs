using Chronicle.Plugin.Kodi.NFO;
using FluentAssertions;
using Xunit;

namespace Chronicle.Plugin.Kodi.NFO.Tests;

public class KodiNfoPluginTests
{
    private readonly KodiNfoPlugin _plugin = new();

    [Fact]
    public void PluginId_MatchesManifest()
    {
        _plugin.PluginId.Should().Be("chronicle.plugin.kodi.nfo");
    }

    [Fact]
    public void GetSupportedMediaTypes_CoversMoviesTvAndFanedits()
    {
        var names = _plugin.GetSupportedMediaTypes().Select(t => t.MediaTypeName).ToList();

        names.Should().Contain(["movies", "tv", "fanedits"]);
    }

    [Fact]
    public void GetSupportedMediaTypes_NeverCreatesNewMediaTypeRows()
    {
        // Empty DisplayName is the "don't upsert a media_types row" signal (see
        // MediaTypeSupport's own doc) -- this plugin only ever reads/writes sidecars for
        // types that already exist, never introduces one of its own.
        _plugin.GetSupportedMediaTypes().Should().OnlyContain(t => t.DisplayName == "");
    }

    [Fact]
    public void GetSettingsSchema_HasNoSettings()
    {
        _plugin.GetSettingsSchema().Settings.Should().BeEmpty();
    }

    [Fact]
    public void Configure_DoesNotThrow()
    {
        var act = () => _plugin.Configure(new Dictionary<string, string>());
        act.Should().NotThrow();
    }
}
