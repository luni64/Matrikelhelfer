using System;
using System.Threading.Tasks;
using Matrikelhelfer.Models;

namespace Matrikelhelfer.Services;

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

    // Extracts the citation fields for the page; null when the URL belongs
    // to this provider but is not a readable scan page.
    Task<MatriculaInfo?> GetInfoAsync(Uri url);

    // Fetches the scan image bytes - per provider, since a provider may need
    // its own headers/session to serve images.
    Task<byte[]> DownloadImageAsync(string imageUrl);
}
