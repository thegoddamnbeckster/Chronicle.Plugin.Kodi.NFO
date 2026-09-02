using System.Text.Json;
using Chronicle.Plugins;
using Chronicle.Plugins.Models;

namespace Chronicle.Plugin.Kodi.NFO;

/// <summary>
/// <see cref="ISidecarFormatPlugin"/> for Kodi's own .nfo sidecar convention -- both directions.
/// Read side (KodiNfoReader) is a straight relocation of Chronicle core's former
/// NfoSignalExtractor/NfoDetailParser (Chronicle.Services.Scan), same behavior. Write side
/// (KodiNfoBuilder) is a C# port of Chronicle_Scraper's lib/nfo_writer.py/tv_nfo_writer.py/
/// nfo_common.py -- everything those two Kodi addons used to build from Chronicle's own
/// resolved data, now built once, here, instead of twice in Python.
///
/// See docs/plans/2026-09-02-kodi-nfo-plugin-design.md in the main Chronicle repo for the
/// full design and the one thing that could NOT move here: <fileinfo><streamdetails> (Kodi's
/// own file-probe data), which the caller still supplies via
/// SidecarBuildRequest.ExtraFields.
/// </summary>
public sealed class KodiNfoPlugin : ISidecarFormatPlugin
{
    // ── Identity ──────────────────────────────────────────────────────────────

    public string PluginId => "chronicle.plugin.kodi.nfo";
    public string Name     => "Kodi NFO";
    public string Version  => "1.0.0";
    public string Author   => "Chronicle Contributors";

    // ── Capability declarations ───────────────────────────────────────────────

    public MediaTypeSupport[] GetSupportedMediaTypes() =>
    [
        // Empty DisplayName -- this plugin never introduces a new media type, it only reads/
        // writes sidecars for types that already exist (see MediaTypeSupport's own doc: empty
        // DisplayName means "don't create a media_types row for this").
        new() { MediaTypeName = "movies", DisplayName = "" },
        new() { MediaTypeName = "tv",     DisplayName = "" },
        new() { MediaTypeName = "fanedits", DisplayName = "" },
    ];

    /// <summary>No configurable settings -- this plugin has no credentials or options.</summary>
    public PluginSettingsSchema GetSettingsSchema() => new();

    // ── Lifecycle ─────────────────────────────────────────────────────────────

    public void Configure(IReadOnlyDictionary<string, string> settings) { /* nothing to configure */ }

    // ── Read side ─────────────────────────────────────────────────────────────

    public string? FindSidecar(string mediaFilePath) =>
        KodiNfoReader.FindSidecar(mediaFilePath);

    public SidecarSignal? ExtractSignal(string sidecarPath) =>
        KodiNfoReader.ExtractSignal(sidecarPath);

    public SidecarCapture? CaptureLossless(string sidecarPath) =>
        KodiNfoReader.CaptureLossless(sidecarPath);

    public JsonElement? ExtractCuratedFields(string sidecarPath) =>
        KodiNfoReader.ExtractCuratedFields(sidecarPath);

    // ── Write side ────────────────────────────────────────────────────────────

    public Task<byte[]> BuildAsync(SidecarBuildRequest request, CancellationToken ct = default) =>
        Task.FromResult(KodiNfoBuilder.Build(request));
}
