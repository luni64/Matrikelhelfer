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
    string SignaturPfarrei = "", string SignaturBuch = "", string PageUrl = "")
{
    // The page-specific link, when the provider offers one distinct from Url.
    // For Matricula/DFG the address-bar Url already IS the page (it carries the
    // scan number), so this stays empty and callers fall back to Url. For
    // ARCHION the Url identifies only the book (the page lives in client-side
    // viewer state); PageUrl holds the page permalink when it could be read.
    [JsonIgnore]
    public string EffectivePageUrl => string.IsNullOrEmpty(PageUrl) ? Url : PageUrl;

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
    // parseable - matches the on-screen "Page ..." line. Scan == 0 means the
    // provider gives no sequential scan number (ARCHION: the page lives in
    // client-side viewer state, not the URL) - then show only the page, if any.
    [JsonIgnore]
    public string PageDescription =>
        Scan > 0
            ? (Page != null ? $"{Page} (Scan {Scan})" : $"Scan {Scan}")
            : (Page ?? "");

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

    // Canonical "is this the same book?" key, derived from BookUrl. Used ONLY
    // for comparison - never displayed, never stored as a link, because it is
    // deliberately lossy. Url/PageUrl keep whatever the user actually opened.
    //
    // The language segment is neutralized because we keep links in the user's
    // language (an English reader gets /en/ links), so the SAME book yields
    // different BookUrls per reader and raw string comparison would under-merge.
    //
    // Query params are DROPPED except the DFG viewer's tx_dlf[id]: everything
    // else there is view state that varies between visits to the same book
    // (`id=9`, `tx_dlf[double]=0`, `cHash`, the page). Real saved data had two
    // finds in one book whose URLs differed only by `id=9` vs
    // `tx_dlf[double]=0` - sorting params would still have split them into two
    // books. tx_dlf[id] must survive because every DFG book shares the path
    // "/show", so the path alone identifies nothing; it is unescaped first
    // since browsers deliver it either raw or percent-encoded.
    [JsonIgnore]
    public string BookKey => NormalizeBookUrl(BookUrl);

    const string DfgBookParam = "tx_dlf[id]";

    static readonly string[] LanguageSegments = ["de", "en"];

    // Public so the BibTeX exporter can normalize the url= fields it reads
    // back out of an existing .bib the same way, and actually match them.
    public static string NormalizeBookUrl(string bookUrl)
    {
        if (string.IsNullOrWhiteSpace(bookUrl))
        {
            return "";
        }
        if (!Uri.TryCreate(bookUrl, UriKind.Absolute, out var uri))
        {
            return bookUrl.Trim().ToLowerInvariant();
        }

        string host = uri.Host.ToLowerInvariant();
        if (host.StartsWith("www.", StringComparison.Ordinal))
        {
            host = host[4..];
        }

        var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length > 0 && LanguageSegments.Contains(segments[0].ToLowerInvariant()))
        {
            segments[0] = "*";
        }

        string book = "";
        foreach (var part in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            int eq = part.IndexOf('=');
            if (eq > 0 &&
                Uri.UnescapeDataString(part[..eq]).Equals(DfgBookParam, StringComparison.OrdinalIgnoreCase))
            {
                book = "?" + Uri.UnescapeDataString(part[(eq + 1)..]);
                break;
            }
        }

        return $"https://{host}/{string.Join('/', segments)}{book}";
    }

    static bool IsPageParam(string param) =>
        param.StartsWith("pg=") ||
        param.StartsWith("tx_dlf[page]=") ||
        param.StartsWith("tx_dlf%5Bpage%5D=", StringComparison.OrdinalIgnoreCase) ||
        param.StartsWith("pageId=", StringComparison.OrdinalIgnoreCase) ||
        param.StartsWith("cHash=", StringComparison.OrdinalIgnoreCase);

    // The site's dates are spelled-out German ("31. Dezember 1625"), always
    // ending in the 4-digit year.
    public static string ExtractYear(string date)
    {
        var m = Regex.Match(date, @"\d{4}$");
        return m.Success ? m.Value : date;
    }
}
