using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Matrikelhelfer.Models;

namespace Matrikelhelfer.Services;

// Fills a citation template's {Placeholder} tokens from a MatriculaInfo.
// Deliberately plain string substitution, not an expression language -
// citation templates only ever need "drop this field in here," and keeping
// the template language this simple is what makes user-authored templates
// (a later feature) safe and easy to explain/document.
static class CitationTemplateEngine
{
    static readonly CultureInfo German = CultureInfo.GetCultureInfo("de-DE");

    // A selectable rendering for the date placeholders ({Von}, {Bis},
    // {AccessDate}). Format == null means "verbatim as the provider wrote
    // it". Only the Id is persisted (per format, in formats.json); Sample
    // doubles as the dropdown's display text.
    public sealed record DateStyle(string Id, string Sample, Func<DateTime, string>? Format)
    {
        public string Apply(string providerDate) =>
            Format is null || !TryParseProviderDate(providerDate, out var date)
                ? providerDate
                : Format(date);
    }

    // Declared BEFORE DateStyles: static initializers run in textual order,
    // and the compiler flags the lambda's array access as possibly-null
    // otherwise.
    static readonly string[] GermanGedcomMonths =
        ["JAN", "FEB", "MÄR", "APR", "MAI", "JUN", "JUL", "AUG", "SEP", "OKT", "NOV", "DEZ"];

    public static readonly IReadOnlyList<DateStyle> DateStyles = new DateStyle[]
    {
        new("original", "Original (wie vom Anbieter)", null),
        new("de-numeric", "22.03.1845", d => d.ToString("dd.MM.yyyy")),
        new("de-long", "22. März 1845", d => d.ToString("d. MMMM yyyy", German)),
        // GEDCOM-style: 3-letter month, uppercase. NOT .NET's German MMM -
        // that keeps "Juni"/"Juli"/"März" unabbreviated, so a fixed table
        // guarantees the 3-letter form.
        new("de-gedcom", "22 MÄR 1845", d => $"{d.Day} {GermanGedcomMonths[d.Month - 1]} {d.Year}"),
        new("iso", "1845-03-22", d => d.ToString("yyyy-MM-dd")),
        new("en-long", "22 March 1845", d => d.ToString("d MMMM yyyy", CultureInfo.InvariantCulture)),
        new("en-us", "Mar 22, 1845", d => d.ToString("MMM d, yyyy", CultureInfo.InvariantCulture)),
        new("en-gedcom", "22 MAR 1845", d => d.ToString("d MMM yyyy", CultureInfo.InvariantCulture).ToUpperInvariant()),
    };

    public static DateStyle DateStyleById(string id) =>
        DateStyles.FirstOrDefault(s => s.Id == id) ?? DateStyles[0];

    // The providers' known date spellings: Matricula "1. Januar 1670",
    // AEM "01.01.1846" (+ ISO, defensively). Unparseable dates pass through
    // verbatim - a citation must never invent a date.
    static bool TryParseProviderDate(string raw, out DateTime date) =>
        DateTime.TryParseExact(raw.Trim(),
            ["d. MMMM yyyy", "d.M.yyyy", "yyyy-MM-dd"],
            German, DateTimeStyles.None, out date);

    public static string Render(CitationStyle style, MatriculaInfo info)
    {
        var dateStyle = DateStyleById(style.DateFormat);
        var now = DateTime.Now;
        // {AccessDate} follows the chosen date format; "original" has no
        // meaning for today's date, so it falls back to German long form.
        string accessDate = dateStyle.Format?.Invoke(now) ?? now.ToString("d. MMMM yyyy", German);

        var values = new Dictionary<string, string>
        {
            ["Land"] = info.Land,
            ["Bistum"] = info.Bistum,
            ["Pfarrei"] = info.Pfarrei,
            ["Buchtyp"] = info.Buchtyp,
            ["Von"] = dateStyle.Apply(info.DatumVon),
            ["Bis"] = dateStyle.Apply(info.DatumBis),
            ["JahrVon"] = MatriculaInfo.ExtractYear(info.DatumVon),
            ["JahrBis"] = MatriculaInfo.ExtractYear(info.DatumBis),
            ["Signatur"] = info.Signatur,
            ["SignaturPfarrei"] = info.SignaturPfarrei,
            ["SignaturBuch"] = info.SignaturBuch,
            // Deliberately NO fallback to the scan number: Page is the
            // user-confirmed handwritten page number, and a citation must
            // not silently pass a scan index off as a page.
            ["Seite"] = info.Page ?? "",
            ["Scan-Nr"] = info.Scan.ToString(),
            ["PageUrl"] = info.Url,
            ["BookUrl"] = info.BookUrl,
            ["ImageUrl"] = info.ImageUrl,
            ["Scan-ID"] = info.ScanLabel,
            ["AccessDate"] = accessDate,
        };

        string result = style.Template;
        foreach (var (key, value) in values)
        {
            result = result.Replace("{" + key + "}", value);
        }
        return result;
    }

    // Shown in the template editor so a user writing their own template
    // knows exactly which tokens are available; the description becomes the
    // chip's tooltip. Keep in sync with the values dictionary above.
    public sealed record Placeholder(string Name, string Description);

    // Grouped into editor rows by topic: place > book > page > links/date.
    public static readonly IReadOnlyList<IReadOnlyList<Placeholder>> PlaceholderGroups = new IReadOnlyList<Placeholder>[]
    {
        new Placeholder[]
        {
            new("Land", "Land, z. B. „Deutschland“"),
            new("Bistum", "Bistum bzw. Archiv, z. B. „Eichstätt, rk Bistum“"),
            new("Pfarrei", "Pfarrei, z. B. „Pollenfeld“"),
            new("SignaturPfarrei", "Signatur des Pfarrei-Bestands (nicht bei Matricula), z. B. „CB481“"),
        },
        new Placeholder[]
        {
            new("SignaturBuch", "Signatur des einzelnen Buchs, z. B. „M7658“"),
            new("Signatur", "Vollständige Signatur, z. B. „3-01“ oder „CB481, M7658“"),
            new("Buchtyp", "Art des Kirchenbuchs, z. B. „Taufen“"),
            new("Von", "Beginn des Buchzeitraums im gewählten Datumsformat, z. B. „1. Januar 1670“"),
            new("Bis", "Ende des Buchzeitraums im gewählten Datumsformat, z. B. „31. Dezember 1736“"),
            new("JahrVon", "Jahr des Buchbeginns, z. B. „1670“"),
            new("JahrBis", "Jahr des Buchendes, z. B. „1736“"),
        },
        new Placeholder[]
        {
            new("Seite", "Handschriftliche Seitennummer - nur was im Feld Seite eingetragen wurde, sonst leer"),
            new("Scan-ID", "Scan-Beschriftung des Anbieters (Feld Scan-ID), z. B. „Pollenfeld 01. 007“; Scan-Nummer, wenn der Anbieter keine Beschriftung pflegt"),
            new("Scan-Nr", "Scan-Nummer im Viewer, z. B. „8“"),
        },
        new Placeholder[]
        {
            new("BookUrl", "Link auf das Buch (ohne Seitenangabe)"),
            new("PageUrl", "Link auf die aktuelle Scan-Seite"),
            new("ImageUrl", "Direktlink auf die Bilddatei des Scans"),
            new("AccessDate", "Heutiges Datum (Zugriffsdatum) im gewählten Datumsformat, z. B. „18. Juli 2026“"),
        },
    };
}
