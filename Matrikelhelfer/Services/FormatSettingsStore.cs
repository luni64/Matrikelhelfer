using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Matrikelhelfer.Models;

namespace Matrikelhelfer.Services;

// The full editable format state: the source-format and citation-format
// lists plus which entry of each is active (rendered into the
// Quellenangabe/Zitatangabe fields).
class FormatSettings
{
    public required List<CitationStyle> SourceFormats { get; init; }
    public required CitationStyle SelectedSource { get; init; }
    public required List<CitationStyle> CitationFormats { get; init; }
    public required CitationStyle SelectedCitation { get; init; }
}

// Persists the user's format templates (and which ones are active) across
// restarts - currently the only state the app stores on disk. Load/Save are
// deliberately best-effort: a missing or corrupt file just yields the
// built-in defaults, and a failed save must never take the app down over
// what is convenience state.
static class FormatSettingsStore
{
    class Payload
    {
        public List<CitationStyle> SourceFormats { get; set; } = new();
        public string? SelectedSourceFormat { get; set; }
        public List<CitationStyle> CitationFormats { get; set; } = new();
        public string? SelectedCitationFormat { get; set; }
    }

    static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Matrikelhelfer", "formats.json");

    public static FormatSettings Load()
    {
        Payload? payload = null;
        try
        {
            payload = JsonSerializer.Deserialize<Payload>(File.ReadAllText(FilePath));
        }
        catch (Exception)
        {
            // No file yet (first start) or unreadable JSON - use the defaults.
        }

        // Each list seeds independently, so a formats.json with one list
        // emptied keeps the other and just regains the seeded default. On a
        // fresh seed the catalog also names which entry starts active.
        var sources = payload?.SourceFormats is { Count: > 0 } s
            ? s
            : CitationStyleCatalog.SourceSeed.ToList();
        var citations = payload?.CitationFormats is { Count: > 0 } c
            ? c
            : CitationStyleCatalog.CitationSeed.ToList();
        return new FormatSettings
        {
            SourceFormats = sources,
            SelectedSource = sources.FirstOrDefault(f =>
                f.Name == (payload?.SelectedSourceFormat ?? CitationStyleCatalog.DefaultSourceName)) ?? sources[0],
            CitationFormats = citations,
            SelectedCitation = citations.FirstOrDefault(f =>
                f.Name == (payload?.SelectedCitationFormat ?? CitationStyleCatalog.DefaultCitationName)) ?? citations[0],
        };
    }

    public static void Save(FormatSettings settings)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            var payload = new Payload
            {
                SourceFormats = settings.SourceFormats,
                SelectedSourceFormat = settings.SelectedSource.Name,
                CitationFormats = settings.CitationFormats,
                SelectedCitationFormat = settings.SelectedCitation.Name,
            };
            File.WriteAllText(FilePath, JsonSerializer.Serialize(
                payload, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception)
        {
            // Best-effort - the formats still apply for this session.
        }
    }
}
