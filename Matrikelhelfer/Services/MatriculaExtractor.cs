using System;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using HtmlAgilityPack;
using Matrikelhelfer.Models;

namespace Matrikelhelfer.Services;

// Pulls citation metadata for a data.matricula-online.eu page out of its
// server-rendered HTML. Book-level fields (diocese/parish/type/date range)
// are cached per URL path, since they only change when the user navigates to
// a different book; the scan/page numbers are cheap to recompute from the
// already-fetched page whenever only the "pg" query parameter changes.
class MatriculaExtractor : ICitationExtractor
{
    const string Host = "data.matricula-online.eu";

    static readonly HttpClient s_http = CreateClient();

    // The page's viewer widget is initialized with a JS object literal (not
    // strict JSON - some values are bare identifiers), so rather than parsing
    // the whole thing we pull out just the "labels" array, which is valid
    // JSON on its own. It holds the priest-written page number for each scan,
    // index-aligned with the "pg" query parameter (labels[pg-1]).
    static readonly Regex LabelsPattern = new(@"""labels""\s*:\s*(\[[^\]]*\])", RegexOptions.Singleline);

    // Same idea as LabelsPattern, but for the "files" array: each entry is
    // "/image/<base64url blob>" - Matricula obfuscates the real image URL
    // rather than exposing it directly. index-aligned with "pg" like labels.
    static readonly Regex FilesPattern = new(@"""files""\s*:\s*(\[[^\]]*\])", RegexOptions.Singleline);
    static readonly Regex TrailingNumber = new(@"(\d+)\s*$");
    static readonly Regex PgParam = new(@"[?&]pg=(\d+)");

    string? _cachedPath;
    HtmlNode? _cachedDoc;
    string[]? _cachedLabels;

    // Raw, still base64url-encoded "/image/<blob>" entries - decoding all
    // ~300 of a book's entries whenever it loads would be wasted work when a
    // user only ever looks at a handful of pages, so only the one entry
    // actually needed (pg-1) gets decoded, on every read.
    string[]? _cachedImageEntries;

    // Fetches the actual scan image bytes, reusing the same UA-configured
    // client as the HTML fetch above (no separate HttpClient instance).
    public async Task<byte[]> DownloadImageAsync(string imageUrl) =>
        await s_http.GetByteArrayAsync(imageUrl);

    public bool CanHandle(Uri url) => url.Host.Equals(Host, StringComparison.OrdinalIgnoreCase);

    // Only the URL is used: Matricula's citation data is all in the server-
    // rendered HTML this extractor fetches (the context's title/link lookup
    // are for login-gated providers).
    public async Task<MatriculaInfo?> GetInfoAsync(PageContext context)
    {
        Uri uri = context.Url;

        // Matricula localizes both the field LABELS ("Buchtyp"/"Signatur"/
        // "Datum von" vs English "Type"/"ID"/"Date from") AND the date VALUES
        // ("1. Januar 1872" vs "Jan. 1, 1872"). This app is German and writes
        // German citations, so always read the /de/ version whatever language
        // the user browses in. The language is the first path segment; swapping
        // it on the GetLeftPart(Path) STRING (not AbsolutePath) preserves the
        // double-encoding of a slash-bearing book id further along, e.g.
        // "106%252F1872" - the site 404s on the single-encoded "%2F" form.
        string authority = uri.GetLeftPart(UriPartial.Authority);
        var segments = uri.GetLeftPart(UriPartial.Path)[authority.Length..].Split('/');
        if (segments.Length > 1 && segments[1].Length == 2)
        {
            segments[1] = "de";
        }
        string path = string.Join('/', segments);

        int pg = 1;
        var pgMatch = PgParam.Match(uri.Query);
        if (pgMatch.Success)
        {
            pg = int.Parse(pgMatch.Groups[1].Value);
        }

        if (path != _cachedPath)
        {
            string html = await s_http.GetStringAsync(authority + path);
            var doc = new HtmlDocument();
            doc.LoadHtml(html);

            _cachedDoc = doc.DocumentNode;
            _cachedLabels = ExtractLabels(html);
            _cachedImageEntries = ExtractImageEntries(html);
            _cachedPath = path;
        }

        if (_cachedDoc == null)
        {
            return null;
        }

        // Same host != book page: parish/diocese overview pages have
        // neither the viewer widget ("labels") nor the book-metadata table
        // (Signatur row). Checking content instead of URL depth keeps this
        // independent of how deep a country's hierarchy nests.
        if (_cachedLabels is null && TableValue(_cachedDoc, "Signatur") is null)
        {
            return null;
        }

        string? page = null;
        string scanLabel = "";
        if (_cachedLabels != null && pg >= 1 && pg <= _cachedLabels.Length)
        {
            scanLabel = _cachedLabels[pg - 1];
            var m = TrailingNumber.Match(scanLabel);
            if (m.Success)
            {
                page = m.Groups[1].Value;
            }
        }

        string imageUrl = "";
        if (_cachedImageEntries != null && pg >= 1 && pg <= _cachedImageEntries.Length)
        {
            string entry = _cachedImageEntries[pg - 1];
            imageUrl = DecodeBase64Url(StripImageEntryDelimiters(entry)) ?? "";
        }

        // Matricula's Signatur ("3-01") already identifies only the book
        // within the parish - there is no parish-level archival signature
        // on the site, so SignaturPfarrei stays empty.
        string signatur = TableValue(_cachedDoc, "Signatur") ?? "";

        return new MatriculaInfo(
            Land: LookupBreadcrumb(_cachedDoc, path, levelsUp: 3) ?? "",
            Bistum: LookupBreadcrumb(_cachedDoc, path, levelsUp: 2) ?? "",
            Pfarrei: LookupBreadcrumb(_cachedDoc, path, levelsUp: 1) ?? "",
            Buchtyp: TableValue(_cachedDoc, "Buchtyp") ?? "",
            DatumVon: TableValue(_cachedDoc, "Datum von") ?? "",
            DatumBis: TableValue(_cachedDoc, "Datum bis") ?? "",
            Signatur: signatur,
            SignaturPfarrei: "",
            SignaturBuch: signatur,
            Scan: pg,
            Page: page,
            ScanLabel: scanLabel,
            Url: uri.OriginalString,
            ImageUrl: imageUrl);
    }

    static string? TableValue(HtmlNode doc, string label)
    {
        var th = doc.SelectSingleNode($"//tr/th[normalize-space(text())='{label}']");
        return th?.SelectSingleNode("following-sibling::td[1]")?.InnerText.Trim();
    }

    // The breadcrumb links display names to their own path level (e.g.
    // ".../eichstaett/" -> "Eichstätt, rk Bistum"), so we look up the parent
    // path (levelsUp segments stripped from the current page's path) instead
    // of a fixed breadcrumb position, since hierarchy depth could vary.
    static string? LookupBreadcrumb(HtmlNode doc, string currentPath, int levelsUp)
    {
        var segments = currentPath.TrimEnd('/').Split('/');
        if (segments.Length <= levelsUp)
        {
            return null;
        }
        string targetPath = string.Join("/", segments[..^levelsUp]) + "/";

        var anchors = doc.SelectNodes("//ul[@class='breadcrumb']/li[@class='breadcrumb-item']/a");
        if (anchors == null)
        {
            return null;
        }

        foreach (var a in anchors)
        {
            if (a.GetAttributeValue("href", "") == targetPath)
            {
                return HtmlEntity.DeEntitize(a.InnerText).Trim();
            }
        }
        return null;
    }

    static string[]? ExtractLabels(string html)
    {
        var match = LabelsPattern.Match(html);
        if (!match.Success)
        {
            return null;
        }
        try
        {
            return JsonSerializer.Deserialize<string[]>(match.Groups[1].Value);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    // The "files" array holds one "/image/<base64url>/" entry per scan (note
    // the trailing slash - it's a URL path separator, not part of the
    // payload); the real image URL is base64url-encoded rather than exposed
    // directly. Returned entries are left encoded - see DecodeBase64Url's
    // call site in GetInfoAsync, which only ever decodes the one page
    // actually requested.
    static string[]? ExtractImageEntries(string html)
    {
        var match = FilesPattern.Match(html);
        if (!match.Success)
        {
            return null;
        }
        try
        {
            return JsonSerializer.Deserialize<string[]>(match.Groups[1].Value);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    // Strips the "/image/" prefix and the trailing "/" that Matricula wraps
    // every "files" entry in - confirmed live: leaving the trailing slash in
    // the payload either throws on decode (it makes the base64 length need 3
    // padding characters, which is invalid) or, depending on the payload's
    // length parity, silently decodes into an extra garbage byte appended to
    // an otherwise-correct URL. Both were previously mistaken for one-off
    // data quirks; this is the actual root cause for both.
    static string StripImageEntryDelimiters(string entry)
    {
        string body = entry.StartsWith("/image/") ? entry["/image/".Length..] : entry;
        return body.EndsWith('/') ? body[..^1] : body;
    }

    // .NET's Convert.FromBase64String expects standard base64 ('+', '/',
    // '='-padded); base64url substitutes '-'/'_' for those and omits
    // padding, so both need restoring first.
    static string? DecodeBase64Url(string base64Url)
    {
        string base64 = base64Url.Replace('-', '+').Replace('_', '/');
        base64 = base64.PadRight(base64.Length + (4 - base64.Length % 4) % 4, '=');
        try
        {
            return Encoding.UTF8.GetString(Convert.FromBase64String(base64));
        }
        catch (FormatException)
        {
            return null;
        }
    }

    static HttpClient CreateClient()
    {
        var client = new HttpClient();
        client.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36");
        return client;
    }
}
