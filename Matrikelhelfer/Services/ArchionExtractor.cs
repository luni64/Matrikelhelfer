using System;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Matrikelhelfer.Models;

namespace Matrikelhelfer.Services;

// Provider for church books shown through ARCHION (www.archion.de), the German
// Protestant church-book portal. Its scans are subscription-only, and - unlike
// Matricula/DFG - even the citation METADATA is login-gated: a cookie-less HTTP
// fetch of a real (paid) book returns a stripped page with no breadcrumb. So
// this extractor does NOT fetch. Instead it reads the browser TAB TITLE, which
// mirrors the logged-in page's <title> and carries the full breadcrumb chain
// (reversed), e.g.:
//
//   Beerdigungsregister 1808-1840 | Bromskirchen | Dekanat Biedenkopf |
//   Zentralarchiv der Ev. Kirche in Hessen und Nassau | Hessen: Kirchenbücher online mit ARCHION
//
// The address-bar URL still supplies the links: {PageUrl} = the URL as shown
// (with ?pageId=… when the user opened a page permalink), {BookUrl} = it minus
// the page params. ARCHION exposes no image URL, no archival signature, and
// keeps the current page in client-side viewer state (not the URL), so those
// stay empty and Seite is manual - as everywhere.
class ArchionExtractor : ICitationExtractor
{
    // ARCHION's site name, appended after "<Land>: " in every book page title.
    // Everything before it is the reversed breadcrumb; anything else (a generic
    // "Viewer" title, another site) is not a readable ARCHION book page.
    const string SiteMarker = "Kirchenbücher online mit ARCHION";

    // "<Buchtyp> <YYYY>-<YYYY>", e.g. "Beerdigungsregister 1808-1840" - the
    // book label (first title segment); captures the type and both years.
    static readonly Regex BookLabelPattern =
        new(@"^(?<typ>.*?)\s+(?<von>\d{4})\s*[-–—]\s*(?<bis>\d{4})\s*$");

    // ARCHION's page permalink ("https://www.archion.de/p/<code>"), shown only
    // in the viewer's permalink panel - never in the URL. Read from the
    // rendered page when the panel is open; it is the exact-page link.
    static readonly Regex PermalinkPattern =
        new(@"https?://(?:www\.)?archion\.de/p/[0-9a-zA-Z]+/?");

    public bool CanHandle(Uri url) =>
        url.Host.EndsWith("archion.de", StringComparison.OrdinalIgnoreCase) &&
        url.AbsolutePath.Contains("/viewer", StringComparison.OrdinalIgnoreCase);

    // ARCHION images are paywalled/session-bound, so ImageUrl is always empty
    // and the image-save action stays disabled - this only satisfies the
    // interface.
    public Task<byte[]> DownloadImageAsync(string imageUrl) =>
        throw new NotSupportedException("ARCHION stellt keine direkten Bilddownloads bereit.");

    // No network call: everything comes from the (logged-in) tab title plus the
    // address-bar URL, and - if the permalink panel is open - the exact-page
    // permalink read from the rendered page.
    public Task<MatriculaInfo?> GetInfoAsync(PageContext page) =>
        Task.FromResult(Parse(page));

    static MatriculaInfo? Parse(PageContext page)
    {
        if (page.Title is not string pageTitle)
        {
            return null;
        }

        // Cut off the site name (and the browser's own " - <Browser>" suffix
        // after it); what remains is "<Buch> | … | <Archiv> | <Land>:".
        int marker = pageTitle.IndexOf(SiteMarker, StringComparison.Ordinal);
        if (marker < 0)
        {
            return null;
        }
        string chain = pageTitle[..marker].TrimEnd().TrimEnd(':').Trim();

        // Reversed breadcrumb: Buch / Pfarrei / [Dekanat|Kirchenkreis…] /
        // Archiv / Land. Mapped from the ends so the varying middle depth
        // (Dekanat in Hessen, Kirchenkreis in Thüringen, sometimes none)
        // doesn't matter. Need at least Buch, Pfarrei, Archiv, Land.
        var parts = chain.Split(" | ",
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length < 4)
        {
            return null;
        }

        string label = parts[0];
        string pfarrei = parts[1];
        string bistum = parts[^2];
        // "Land" here is ARCHION's top level (a Bundesland/Landeskirche region),
        // not a country - unlike Matricula/DFG, which put the country in {Land}.
        string land = parts[^1];

        var m = BookLabelPattern.Match(label);
        string buchtyp = m.Success ? m.Groups["typ"].Value.Trim() : label;
        string von = m.Success ? m.Groups["von"].Value : "";
        string bis = m.Success ? m.Groups["bis"].Value : "";

        // The address-bar URL identifies only the book (BookUrl derives from
        // it). The page-specific link is the permalink read from the rendered
        // panel, if open; empty otherwise, so {PageUrl} falls back to the URL.
        string permalink = page.FindLink(PermalinkPattern) ?? "";

        return new MatriculaInfo(
            Land: land,
            Bistum: bistum,
            Pfarrei: pfarrei,
            Buchtyp: buchtyp,
            DatumVon: von,
            DatumBis: bis,
            Signatur: "",            // ARCHION publishes no archival signature
            SignaturPfarrei: "",
            SignaturBuch: "",
            Scan: 0,                 // no sequential scan number in the URL
            Page: null,
            ScanLabel: "",           // no provider scan label either
            Url: page.Url.OriginalString,
            ImageUrl: "",
            PageUrl: permalink);
    }
}
