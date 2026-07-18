using System.Collections.Generic;
using Matrikelhelfer.Models;

namespace Matrikelhelfer.Services;

// The first-start seed for both format lists (only consulted when
// formats.json is missing or a list in it is empty) - curated by the user
// from real research use; keep in sync with what a fresh formats.json
// should look like.
static class CitationStyleCatalog
{
    // Fixed genealogy-software split: the "source" is the book, the
    // "citation" is the specific page/scan on it - matching how genealogy
    // software keeps one source record per church book plus per-page
    // citations attached to it.
    public static readonly IReadOnlyList<CitationStyle> SourceSeed = new[]
    {
        new CitationStyle(
            "Sehr kurz",
            "{Pfarrei}, {Buchtyp} {JahrVon}–{JahrBis}, {Signatur}"),

        new CitationStyle(
            "Kurz",
            "{Bistum}, {Pfarrei}, {Buchtyp} {JahrVon}–{JahrBis}, Signatur {Signatur}, S. {Seite}."),

        new CitationStyle(
            "Ausführlich",
            "{Pfarrei} ({Bistum}), \"{Buchtyp} {Von} – {Bis},\" Signatur {Signatur}, " +
            "p. {Seite}; (Online: {PageUrl} : Zugriff am {AccessDate})."),

        new CitationStyle(
            "Chicago (Notes-Bibliography)",
            "{Buchtyp} ({Von}–{Bis}), Signatur {Signatur}, {Pfarrei} Parish Register, " +
            "{Bistum}, Matricula Online, {PageUrl}.",
            DateFormat: "en-long"),
    };

    public const string DefaultSourceName = "Kurz";

    // No URL in the page citations - genealogy software has its own link
    // field, and the user copies that from the Links section.
    public static readonly IReadOnlyList<CitationStyle> CitationSeed = new[]
    {
        new CitationStyle("Standard", "Seite: {Seite}, ScanID: {Scan-ID}"),
        new CitationStyle("Nur Seite", "S.{Seite}"),
        new CitationStyle("Kurz", "S.{Seite}, ID: {Scan-ID}"),
    };

    public const string DefaultCitationName = "Kurz";
}
