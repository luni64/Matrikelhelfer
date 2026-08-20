using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Matrikelhelfer.Models;

namespace Matrikelhelfer.Services;

// Writes the saved finds as a CSV for the user's own long-term archive
// (e.g. checking years later whether a book was already worked through).
// Format targets German Excel: semicolon separator (comma is the decimal
// separator locale-side, Excel expects ';') and a UTF-8 BOM (without it
// Excel guesses ANSI and mangles umlauts on double-click).
static class EntryCsvExporter
{
    const char Separator = ';';

    public static void Export(
        IEnumerable<LibraryEntry> records, string path,
        CitationStyle sourceFormat, CitationStyle citationFormat)
    {
        var sb = new StringBuilder();

        // No Name column since the field was removed (2026-08) - old names
        // were folded into the comment by the library migration.
        AppendRow(sb,
            "Gespeichert", "Kommentar",
            "Land", "Bistum", "Pfarrei", "Buchtyp", "DatumVon", "DatumBis",
            "Signatur", "SignaturPfarrei", "SignaturBuch",
            "Seite", "Scan-Nr", "Scan-ID", "SeitenUrl", "BuchUrl", "BildUrl",
            "Quellenangabe", "Zitatangabe");

        foreach (var r in records)
        {
            var i = r.Info;
            AppendRow(sb,
                r.SavedAt.ToString("yyyy-MM-dd HH:mm"), r.Comment,
                i.Land, i.Bistum, i.Pfarrei, i.Buchtyp, i.DatumVon, i.DatumBis,
                i.Signatur, i.SignaturPfarrei, i.SignaturBuch,
                i.Page ?? "", i.Scan.ToString(), i.ScanLabel, i.Url, i.BookUrl, i.ImageUrl,
                // Rendered with the currently active formats - the stored
                // data stays raw (display always re-renders), but an export
                // is a snapshot for outside the app, so the human-readable
                // citations belong in it.
                CitationTemplateEngine.Render(sourceFormat, i),
                CitationTemplateEngine.Render(citationFormat, i));
        }

        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
    }

    static void AppendRow(StringBuilder sb, params string[] fields) =>
        sb.AppendLine(string.Join(Separator, fields.Select(Escape)));

    // Standard CSV quoting: wrap when the field contains the separator,
    // quotes, or line breaks (comments can be multi-line), doubling any
    // embedded quotes.
    static string Escape(string field) =>
        field.IndexOfAny([Separator, '"', '\r', '\n']) < 0
            ? field
            : "\"" + field.Replace("\"", "\"\"") + "\"";
}
