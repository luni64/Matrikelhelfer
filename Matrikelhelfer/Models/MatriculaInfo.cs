using System;
using System.Linq;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Matrikelhelfer.Models;

// The computed properties below carry [JsonIgnore]: System.Text.Json would
// otherwise WRITE them into entries.json (get-only properties serialize but
// never deserialize), bloating the file with derived values that are always
// recomputed from the positional fields on load anyway.
// Signatur is the combined/display value ("CB481, M7658" for AEM, "3-01"
// for Matricula). SignaturPfarrei/SignaturBuch split it where the archive
// distinguishes the parish holding from the individual book (AEM: "CB481" /
// "M7658"); Matricula assigns no parish-level signature, so there only
// SignaturBuch is filled (= Signatur). The trailing defaults keep old
// entries.json files (written before the split) deserializing cleanly.
record MatriculaInfo(
    string Land, string Bistum, string Pfarrei, string Buchtyp, string DatumVon, string DatumBis,
    string Signatur, int Scan, string? Page, string ScanLabel, string Url, string ImageUrl,
    string SignaturPfarrei = "", string SignaturBuch = "")
{
    // A short title identifying this book, e.g. "Titting Taufen 1599-1625" -
    // book-level only (no scan/page reference, unlike the saved-entries list
    // display), matching genealogy convention where the "source" is the book
    // and the specific page is a separate citation detail.
    [JsonIgnore]
    public string CitationTitle =>
        $"{Pfarrei} {Buchtyp} {ExtractYear(DatumVon)}-{ExtractYear(DatumBis)}";

    // Same as CitationTitle but without the parish - used where Pfarrei is
    // already shown as its own field, e.g. "Taufen 1599-1625".
    [JsonIgnore]
    public string BookLabel => $"{Buchtyp} {ExtractYear(DatumVon)}-{ExtractYear(DatumBis)}";

    // "285 (Scan 7)" or "Scan 7" if the priest-written page number wasn't
    // parseable - matches the on-screen "Page ..." line.
    [JsonIgnore]
    public string PageDescription =>
        Page != null ? $"{Page} (Scan {Scan})" : $"Scan {Scan}";

    // Url without the page-position parameters - links the book, not the
    // specific scan. Only the page params (Matricula's ?pg=, the DFG
    // viewer's tx_dlf[page]=) are removed, NOT the whole query: the DFG
    // viewer's tx_dlf[id]= carries the METS reference and must survive.
    // TYPO3's cHash is dropped too - it is computed over the full param set
    // and would be stale once the page param is gone.
    [JsonIgnore]
    public string BookUrl
    {
        get
        {
            int i = Url.IndexOf('?');
            if (i < 0)
            {
                return Url;
            }
            var kept = Url[(i + 1)..].Split('&').Where(p => !IsPageParam(p)).ToArray();
            return kept.Length == 0 ? Url[..i] : Url[..i] + "?" + string.Join("&", kept);
        }
    }

    static bool IsPageParam(string param) =>
        param.StartsWith("pg=") ||
        param.StartsWith("tx_dlf[page]=") ||
        param.StartsWith("tx_dlf%5Bpage%5D=", StringComparison.OrdinalIgnoreCase) ||
        param.StartsWith("cHash=", StringComparison.OrdinalIgnoreCase);

    // The site's dates are spelled-out German ("31. Dezember 1625"), always
    // ending in the 4-digit year.
    public static string ExtractYear(string date)
    {
        var m = Regex.Match(date, @"\d{4}$");
        return m.Success ? m.Value : date;
    }
}
