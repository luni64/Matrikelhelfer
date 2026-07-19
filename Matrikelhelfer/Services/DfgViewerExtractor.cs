using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using System.Xml.Linq;
using Matrikelhelfer.Models;

namespace Matrikelhelfer.Services;

// Provider for church books shown through the DFG viewer (TYPO3/Kitodo
// "tx_dlf" frontend, e.g. dfg-viewer.de displaying the Erzbistum München
// digital archive). The viewer page itself is never fetched - everything
// lives in the METS XML the tx_dlf[id] query parameter points to: MODS
// metadata (book fields), a physical structMap (page order/labels), and a
// fileSec with direct per-page image URLs (no obfuscation, unlike
// Matricula). tx_dlf[page] carries the current scan number, mirroring
// Matricula's ?pg=.
class DfgViewerExtractor : ICitationExtractor
{
    static readonly XNamespace Mets = "http://www.loc.gov/METS/";
    static readonly XNamespace Mods = "http://www.loc.gov/mods/v3";
    static readonly XNamespace Dv = "http://dfg-viewer.de/";
    static readonly XNamespace Xlink = "http://www.w3.org/1999/xlink";

    static readonly HttpClient s_http = CreateClient();

    // The METS has no country field, and its dv:owner names the archive
    // ("Archiv des Erzbistums ..."), not the diocese - this table derives
    // both citation fields from the owner string. Archives not listed yet
    // fall back to Land empty / owner verbatim; add them as encountered.
    static readonly Dictionary<string, (string Land, string Bistum)> OwnerLookup = new()
    {
        ["Archiv des Erzbistums München und Freising"] = ("Deutschland", "Erzbistum München und Freising"),
    };

    // Book-level data parsed from one METS document.
    sealed record BookData(
        string Pfarrei, string Buchtyp, string DatumVon, string DatumBis,
        string Signatur, string SignaturPfarrei, string SignaturBuch,
        string Land, string Bistum, string[] ImageUrls, string[] Labels);

    // Every book visited this session, keyed by METS URL - one fetch per
    // book, ever: page-turning only changes tx_dlf[page] (an array lookup),
    // and returning to an earlier book hits the dictionary. No eviction on
    // purpose: a parsed book is ~100 KB, so even a long session over dozens
    // of books stays trivially small. A null value records a METS that
    // fetched but didn't parse - structurally broken won't heal, so don't
    // re-hammer the archive (fetch *errors* throw and are not cached, they
    // may be transient).
    readonly Dictionary<string, BookData?> _books = new();

    // Routed by query shape, not host: any tx_dlf viewer instance (the
    // central dfg-viewer.de or an archive's self-hosted one) carries the
    // METS reference the same way.
    public bool CanHandle(Uri url) => ParseQuery(url.Query).ContainsKey("tx_dlf[id]");

    public async Task<byte[]> DownloadImageAsync(string imageUrl) =>
        await s_http.GetByteArrayAsync(imageUrl);

    // Only the URL is used: the DFG viewer's data comes from the METS XML that
    // tx_dlf[id] points to, fetched by this extractor.
    public async Task<MatriculaInfo?> GetInfoAsync(PageContext page)
    {
        Uri uri = page.Url;
        var query = ParseQuery(uri.Query);
        if (!query.TryGetValue("tx_dlf[id]", out var metsUrl))
        {
            return null;
        }

        int pg = query.TryGetValue("tx_dlf[page]", out var pgText) &&
                 int.TryParse(pgText, out int parsed) ? parsed : 1;

        if (!_books.TryGetValue(metsUrl, out var cached))
        {
            string xml = await s_http.GetStringAsync(metsUrl);
            cached = ParseMets(xml);
            _books[metsUrl] = cached;
        }

        if (cached is not BookData book)
        {
            return null;
        }

        string imageUrl = pg >= 1 && pg <= book.ImageUrls.Length ? book.ImageUrls[pg - 1] : "";

        // Unlike Matricula, AEM's METS carries no per-page labels
        // (ORDERLABEL/LABEL) - fall back to the bare scan number from the
        // URL so the Seiten-ID field/{PageId} and the image filename still
        // identify the page. Deliberately no "Scan" prefix: labels are raw
        // data, any prefix is the formats' job (a hardcoded one clashes
        // with templates that already write their own in front).
        string scanLabel = pg >= 1 && pg <= book.Labels.Length ? book.Labels[pg - 1] : "";
        if (scanLabel.Length == 0)
        {
            scanLabel = pg.ToString();
        }

        return new MatriculaInfo(
            Land: book.Land,
            Bistum: book.Bistum,
            Pfarrei: book.Pfarrei,
            Buchtyp: book.Buchtyp,
            DatumVon: book.DatumVon,
            DatumBis: book.DatumBis,
            Signatur: book.Signatur,
            SignaturPfarrei: book.SignaturPfarrei,
            SignaturBuch: book.SignaturBuch,
            Scan: pg,
            Page: null,
            ScanLabel: scanLabel,
            Url: uri.OriginalString,
            ImageUrl: imageUrl);
    }

    static BookData? ParseMets(string xml)
    {
        var doc = XDocument.Parse(xml);
        var mods = doc.Descendants(Mods + "mods").FirstOrDefault();
        if (mods is null)
        {
            return null;
        }

        // The book's own title ("Taufen") is a DIRECT child titleInfo - the
        // one nested in relatedItem[@type=host] is the parent holding
        // ("Bestand: ...") and must not shadow it.
        string buchtyp = mods.Elements(Mods + "titleInfo")
            .Elements(Mods + "title").FirstOrDefault()?.Value.Trim() ?? "";

        string pfarrei = Identifier(mods, "Bestand-Name") ?? "";
        string signatur = Identifier(mods, "VE-Signatur")
            ?? mods.Descendants(Mods + "shelfLocator").FirstOrDefault()?.Value.Trim()
            ?? "";

        // The combined signature ("CB481, M7658") splits into the parish
        // holding's signature (Bestand "CB481") and the individual book's
        // ("M7658") - the METS carries the holding part separately, and the
        // book part is the combined value minus that prefix.
        string signaturPfarrei = Identifier(mods, "Bestand-Signatur") ?? "";
        string signaturBuch =
            signaturPfarrei.Length > 0 && signatur.StartsWith(signaturPfarrei + ",")
                ? signatur[(signaturPfarrei.Length + 1)..].Trim()
                : signatur;

        var dates = mods.Elements(Mods + "originInfo").Elements(Mods + "dateCreated").ToList();
        string datumVon = DateByPoint(dates, "start") ?? "";
        string datumBis = DateByPoint(dates, "end") ?? "";

        string owner = doc.Descendants(Dv + "owner").FirstOrDefault()?.Value.Trim() ?? "";
        (string land, string bistum) = OwnerLookup.TryGetValue(owner, out var known)
            ? known
            : ("", owner);

        // fileSec: FILEID -> direct image URL; physical structMap: the page
        // sequence (ORDER-sorted) referencing those FILEIDs. ORDERLABEL/
        // LABEL, when an archive provides them, play Matricula's scan-label
        // role; this archive only numbers pages, so they stay empty.
        var hrefById = doc.Descendants(Mets + "fileGrp")
            .Where(g => (string?)g.Attribute("USE") == "DEFAULT")
            .Descendants(Mets + "file")
            .ToDictionary(
                f => (string?)f.Attribute("ID") ?? "",
                f => (string?)f.Element(Mets + "FLocat")?.Attribute(Xlink + "href") ?? "");

        var pages = doc.Descendants(Mets + "structMap")
            .Where(m => (string?)m.Attribute("TYPE") == "PHYSICAL")
            .Descendants(Mets + "div")
            .Where(d => (string?)d.Attribute("TYPE") == "page")
            .OrderBy(d => (int?)d.Attribute("ORDER") ?? 0)
            .ToList();

        string[] imageUrls = pages
            .Select(d => (string?)d.Element(Mets + "fptr")?.Attribute("FILEID") ?? "")
            .Select(id => hrefById.GetValueOrDefault(id, ""))
            .ToArray();

        string[] labels = pages
            .Select(d => ((string?)d.Attribute("ORDERLABEL") ?? (string?)d.Attribute("LABEL") ?? "").Trim())
            .ToArray();

        return new BookData(pfarrei, buchtyp, datumVon, datumBis, signatur, signaturPfarrei, signaturBuch, land, bistum, imageUrls, labels);
    }

    static string? Identifier(XElement mods, string type) =>
        mods.Elements(Mods + "identifier")
            .FirstOrDefault(e => (string?)e.Attribute("type") == type)?.Value.Trim();

    static string? DateByPoint(List<XElement> dates, string point) =>
        dates.FirstOrDefault(d => (string?)d.Attribute("point") == point)?.Value.Trim();

    // Query keys arrive percent-encoded or not depending on the browser
    // ("tx_dlf[id]" vs "tx_dlf%5Bid%5D") - unescaping both key and value
    // normalizes that away.
    static Dictionary<string, string> ParseQuery(string query)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (string pair in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            int eq = pair.IndexOf('=');
            if (eq > 0)
            {
                result[Uri.UnescapeDataString(pair[..eq])] = Uri.UnescapeDataString(pair[(eq + 1)..]);
            }
        }
        return result;
    }

    static HttpClient CreateClient()
    {
        var client = new HttpClient();
        client.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36");
        return client;
    }
}
