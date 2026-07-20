using System;
using System.Text.Json.Serialization;

namespace Matrikelhelfer.Models;

// One church-book page, as persisted. Holds everything scraped about the page
// and its book (in Info) PLUS the one field the user edits: the handwritten
// page number (Info.Page / "Seite").
//
// Why a page is its own object: several Findings can sit on one page (two
// people in the same register opening), and Seite is a property of the PAGE,
// not of any one find. Storing it per-find - as the pre-2.0 flat SavedRecord
// did - let copies drift apart, so correcting the number on one entry left
// the others stale. Stored once here, every Finding referencing this page
// sees the correction for free.
//
// Book/parish/diocese are NOT separate entities: they are pure scrape,
// re-derived correctly on every read, so duplicating them per page is
// harmless. The Land -> Bistum -> Pfarrei -> Buch tree is built at display
// time by grouping (see BookKey). A level earns entity status only once the
// user can hand-edit it - the same argument that promoted Seite.
record StoredPage(Guid Id, MatriculaInfo Info)
{
    // The trimmed handwritten page number, or null when unknown.
    [JsonIgnore]
    public string? Seite => string.IsNullOrWhiteSpace(Info.Page) ? null : Info.Page.Trim();

    [JsonIgnore]
    public string? Identity => IdentityOf(Info);

    // "Is this the same page?" - one rule with fallbacks:
    //   Scan when > 0; else Seite when non-empty; else NONE.
    //
    // Matricula/DFG take the first branch. ARCHION has no scan number and its
    // permalink encodes zoom/pan (a VIEW url, not a page id), so the
    // user-entered Seite is the only page-level discriminator available -
    // acceptable because a find without a page reference can't be cited
    // anyway. Null means "no identity": every such read becomes a new page,
    // and pages can never be merged or deduplicated.
    //
    // Consequence on ARCHION: identity depends on a MUTABLE field, so
    // correcting Seite changes which page the object is - the caller must
    // handle the merge when the new number already exists (see LibraryStore
    // callers in MainViewModel).
    public static string? IdentityOf(MatriculaInfo info)
    {
        if (info.Scan > 0)
        {
            return $"{info.BookKey}|s{info.Scan}";
        }
        string? seite = string.IsNullOrWhiteSpace(info.Page) ? null : info.Page.Trim();
        return seite is null ? null : $"{info.BookKey}|p{seite}";
    }
}
