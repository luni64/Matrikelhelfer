using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Matrikelhelfer.Models;

namespace Matrikelhelfer.Services;

// Writes the saved finds as a BibTeX .bib SOURCE library: one @misc entry per
// unique church BOOK, rendered with the user's "BibTeX" source format. A .bib
// is a bibliography of sources - many finds share a book, and per-page
// citations belong in the user's LaTeX document, not here - so finds are
// deduplicated down to their books. Cite keys must be unique or the file won't
// compile; distinct books that produce the same key (ARCHION has no signature,
// so its keys end in "_EMPTY" and can collide) get an a/b/c suffix.
static class BibTexExporter
{
    // The cite key: between "@type{" and the first comma of the entry.
    static readonly Regex KeyPattern = new(@"(?<prefix>@\w+\{)(?<key>[^,\r\n]+)");

    public static void Export(IEnumerable<LibraryEntry> records, string path, CitationStyle bibFormat)
    {
        // One entry per unique book: group by BookKey (the normalized,
        // page-less book identity - so the same book read in German and in
        // English collapses into one @misc instead of two); a find with no
        // book URL falls back to its own id so it is never merged with
        // another. Render each book once.
        var entries = records
            .GroupBy(r => string.IsNullOrEmpty(r.Info.BookKey)
                ? r.Finding.Id.ToString()
                : r.Info.BookKey)
            .Select(g => CitationTemplateEngine.Render(bibFormat, g.First().Info).Trim())
            .Where(text => text.Length > 0)
            .Distinct()
            .OrderBy(KeyOf, StringComparer.Ordinal)
            .ToList();

        // Make keys unique: a distinct book that reuses a key takes the next
        // free a, b, c, … suffix.
        var used = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 0; i < entries.Count; i++)
        {
            string key = KeyOf(entries[i]);
            string unique = key;
            for (char suffix = 'a'; !used.Add(unique); suffix++)
            {
                unique = key + suffix;
            }
            if (unique != key)
            {
                entries[i] = KeyPattern.Replace(entries[i], m => m.Groups["prefix"].Value + unique, 1);
            }
        }

        // LaTeX toolchains read UTF-8; no BOM (unlike the Excel-targeted CSV).
        File.WriteAllText(path, string.Join("\n\n", entries) + "\n", new UTF8Encoding(false));
    }

    static string KeyOf(string entry)
    {
        var match = KeyPattern.Match(entry);
        return match.Success ? match.Groups["key"].Value.Trim() : "";
    }
}
