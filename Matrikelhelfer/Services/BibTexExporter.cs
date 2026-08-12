using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Matrikelhelfer.Models;

namespace Matrikelhelfer.Services;

// One book that was written to the .bib. RenamedFrom is set when the cite key
// had to take an a/b/c suffix - the single most important thing to report,
// since a \cite in an existing .tex would otherwise silently miss.
record BibTexAdded(string Key, string Title, string? RenamedFrom);

// One book that was already in the file. Note explains HOW it was recognized,
// which is what saves searching a large .bib by hand.
record BibTexSkipped(string Key, string Title, string Note);

record BibTexReport(
    int Books,
    IReadOnlyList<BibTexAdded> Added,
    IReadOnlyList<BibTexSkipped> Skipped,
    IReadOnlyList<string> Warnings,
    string? LogPath,
    string? LogError);

// Writes the saved finds as a BibTeX .bib SOURCE library: one @misc entry per
// unique church BOOK, rendered with the user's "BibTeX" source format. A .bib
// is a bibliography of sources - many finds share a book, and per-page
// citations belong in the user's LaTeX document, not here - so finds are
// deduplicated down to their books.
//
// Appending to an existing .bib is the realistic workflow (a bibliography
// accrues over a career), so the exporter can add only what is missing and
// never rewrites what is already there - hand-written entries, other source
// types and edited cite keys all survive untouched.
static class BibTexExporter
{
    // The cite key: between "@type{" and the first comma of the entry.
    static readonly Regex KeyPattern = new(@"(?<prefix>@\w+\{)(?<key>[^,\r\n]+)");

    // Entry headers in an EXISTING file - used to learn which books are
    // already present. Deliberately tolerant: any @type, any key shape.
    static readonly Regex EntryHeader = new(@"@(?<type>\w+)\s*\{\s*(?<key>[^,\s{}]+)\s*,");

    static readonly Regex UrlField = new(@"\burl\s*=\s*[{""](?<url>[^}""]*)[}""]",
        RegexOptions.IgnoreCase);

    // @string/@comment/@preamble carry no cite key - never treat them as books.
    static readonly string[] NonEntryTypes = ["string", "comment", "preamble"];

    const string LogFileName = "Matrikelhelfer_Export.log";

    public static BibTexReport Export(
        IEnumerable<LibraryEntry> records, string path, CitationStyle bibFormat, bool append)
    {
        // One entry per unique book: group by BookKey (the normalized,
        // page-less book identity - so the same book read in German and in
        // English collapses into one @misc instead of two); a find with no
        // book URL falls back to its own id so it is never merged.
        var books = records
            .GroupBy(r => string.IsNullOrEmpty(r.Info.BookKey) ? r.Finding.Id.ToString() : r.Info.BookKey)
            .Select(g => g.First())
            .ToList();

        bool appending = append && File.Exists(path);
        string existingText = appending ? File.ReadAllText(path) : "";
        var existing = ParseEntries(existingText);

        // Keys already taken in the file must be respected when suffixing, or
        // a new book can collide with an unrelated entry that is already there.
        var used = new HashSet<string>(existing.Select(e => e.Key), StringComparer.Ordinal);

        var added = new List<BibTexAdded>();
        var skipped = new List<BibTexSkipped>();
        var warnings = new List<string>();
        var texts = new List<string>();

        foreach (var book in books.OrderBy(b => b.Info.CitationTitle, StringComparer.CurrentCulture))
        {
            string title = book.Info.CitationTitle;
            string text = CitationTemplateEngine.Render(bibFormat, book.Info).Trim();
            if (text.Length == 0)
            {
                warnings.Add($"{title}: Format ergab einen leeren Eintrag – übersprungen.");
                continue;
            }

            string key = KeyOf(text);
            string url = MatriculaInfo.NormalizeBookUrl(book.Info.BookUrl);

            // The URL identifies the book more reliably than the key, which the
            // user is explicitly free to rename. Where both sides have one, it
            // decides; otherwise fall back to the key.
            var byUrl = url.Length == 0
                ? null
                : existing.FirstOrDefault(e => e.Url.Length > 0 && e.Url == url);
            if (byUrl is not null)
            {
                skipped.Add(new BibTexSkipped(byUrl.Key, title,
                    byUrl.Key == key
                        ? "Schlüssel und URL gefunden"
                        : $"URL gefunden, Schlüssel in Datei abweichend: \"{byUrl.Key}\""));
                continue;
            }

            bool keyTaken = used.Contains(key);
            if (keyTaken && url.Length == 0)
            {
                // No URL to tell "same book" from "different book, same key"
                // apart - treat a key match as already present.
                skipped.Add(new BibTexSkipped(key, title, "Schlüssel gefunden"));
                continue;
            }

            string unique = key;
            for (char suffix = 'a'; !used.Add(unique); suffix++)
            {
                unique = key + suffix;
            }
            if (unique != key)
            {
                text = KeyPattern.Replace(text, m => m.Groups["prefix"].Value + unique, 1);
            }
            added.Add(new BibTexAdded(unique, title, unique == key ? null : key));

            if (key.EndsWith("_EMPTY", StringComparison.Ordinal))
            {
                warnings.Add($"{title}: keine Signatur – Schlüssel endet auf \"_EMPTY\".");
            }
            texts.Add(text);
        }

        WriteBib(path, texts, appending, existingText);

        string? logPath = null;
        string? logError = null;
        try
        {
            logPath = WriteLog(path, bibFormat, appending, records, books.Count, added, skipped, warnings);
        }
        catch (Exception ex)
        {
            // Best-effort: the .bib is the product, the log is a convenience.
            logError = ex.Message;
        }

        return new BibTexReport(books.Count, added, skipped, warnings, logPath, logError);
    }

    // Appending never rewrites existing content - only adds at the end, so
    // hand-written entries and formatting survive.
    static void WriteBib(string path, List<string> texts, bool appending, string existingText)
    {
        var utf8 = new UTF8Encoding(false);   // LaTeX toolchains read UTF-8; no BOM
        if (!appending)
        {
            File.WriteAllText(path, string.Join("\n\n", texts) + (texts.Count > 0 ? "\n" : ""), utf8);
            return;
        }
        if (texts.Count == 0)
        {
            return;
        }
        string separator = existingText.Length == 0 || existingText.EndsWith("\n\n", StringComparison.Ordinal)
            ? ""
            : existingText.EndsWith("\n", StringComparison.Ordinal) ? "\n" : "\n\n";
        File.AppendAllText(path, separator + string.Join("\n\n", texts) + "\n", utf8);
    }

    record ExistingEntry(string Key, string Url);

    // Splits an existing .bib into entries by header position and pulls the
    // key plus any url field out of each. Deliberately not a full BibTeX
    // parser: it only has to recognize what is already there.
    static List<ExistingEntry> ParseEntries(string text)
    {
        var result = new List<ExistingEntry>();
        var headers = EntryHeader.Matches(text);
        for (int i = 0; i < headers.Count; i++)
        {
            if (NonEntryTypes.Contains(headers[i].Groups["type"].Value.ToLowerInvariant()))
            {
                continue;
            }
            int start = headers[i].Index;
            int end = i + 1 < headers.Count ? headers[i + 1].Index : text.Length;
            var url = UrlField.Match(text[start..end]);
            result.Add(new ExistingEntry(
                headers[i].Groups["key"].Value.Trim(),
                url.Success ? MatriculaInfo.NormalizeBookUrl(url.Groups["url"].Value.Trim()) : ""));
        }
        return result;
    }

    // A running journal next to the .bib. Appends one dated block per run:
    // over a career a bibliography gets large, and "when did I add this book,
    // and under which key?" is otherwise an expensive question.
    static string WriteLog(
        string bibPath, CitationStyle format, bool appending,
        IEnumerable<LibraryEntry> records, int books,
        List<BibTexAdded> added, List<BibTexSkipped> skipped, List<string> warnings)
    {
        string logPath = Path.Combine(
            Path.GetDirectoryName(bibPath) ?? ".", LogFileName);

        var sb = new StringBuilder();
        sb.AppendLine($"=== {DateTime.Now:yyyy-MM-dd HH:mm} · " +
                      $"{(appending ? "Anhängen an" : "Neu geschrieben")} {Path.GetFileName(bibPath)} ===");
        sb.AppendLine($"Format: {format.Name} · {records.Count()} Funde → {books} Bücher");
        sb.AppendLine();

        sb.AppendLine($"Neu hinzugefügt ({added.Count}):");
        foreach (var a in added)
        {
            sb.AppendLine($"  + {a.Key,-28} {a.Title}");
            if (a.RenamedFrom is not null)
            {
                sb.AppendLine($"      ! Schlüssel \"{a.RenamedFrom}\" war belegt, umbenannt zu \"{a.Key}\"");
            }
        }
        sb.AppendLine();

        sb.AppendLine($"Bereits vorhanden ({skipped.Count}):");
        foreach (var s in skipped)
        {
            sb.AppendLine($"  = {s.Key,-28} {s.Title}  ({s.Note})");
        }

        if (warnings.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine($"Warnungen ({warnings.Count}):");
            foreach (string w in warnings)
            {
                sb.AppendLine($"  ! {w}");
            }
        }
        sb.AppendLine();

        File.AppendAllText(logPath, sb.ToString(), new UTF8Encoding(false));
        return logPath;
    }

    static string KeyOf(string entry)
    {
        var match = KeyPattern.Match(entry);
        return match.Success ? match.Groups["key"].Value.Trim() : "";
    }
}
