using System;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Matrikelhelfer.Models;

namespace Matrikelhelfer.Services;

// The page the user is reading, as captured at Lesen time.
// - Url:      the address-bar URL.
// - Title:    the browser tab title (some providers' data lives there, not in
//             the fetched HTML - ARCHION puts its whole breadcrumb in it).
// - FindLink: walks the rendered page's UI Automation tree for a link matching
//             a provider-supplied pattern. Fragile and comparatively slow, so
//             only providers that must (ARCHION, for its page permalink, which
//             the URL never carries) call it; it returns null when not found.
record PageContext(Uri Url, string? Title, Func<Regex, string?> FindLink);

// One church-book provider's extraction strategy. All providers yield the
// same MatriculaInfo shape; they differ only in which pages they understand
// (CanHandle, decided by URL - host or query shape) and how the fields are
// scraped from them.
// Mirrors the IBrowserAddressBarLocator pattern: adding a provider = one new
// class plus one registration line in MainViewModel's extractor list.
interface ICitationExtractor
{
    // Whether this provider is responsible for the given page URL.
    // GetInfoAsync is only called after CanHandle returned true.
    bool CanHandle(Uri url);

    // Extracts the citation fields for the page; null when the URL belongs to
    // this provider but is not a readable scan page. The page context carries
    // the URL plus the extras (tab title, on-demand page-link lookup) that
    // some providers need beyond the URL.
    Task<MatriculaInfo?> GetInfoAsync(PageContext page);

    // Fetches the scan image bytes - per provider, since a provider may need
    // its own headers/session to serve images.
    Task<byte[]> DownloadImageAsync(string imageUrl);
}
