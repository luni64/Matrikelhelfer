using System;

namespace Matrikelhelfer.Models;

// One find: a record spotted on a scan, with the user's own notes. THIS is
// the unit the user creates - not the page. Several Findings on one page are
// normal and expected (two baptisms in the same register opening), which is
// why saving can never mean "one entry per page".
//
// Comment is the find's whole payload since the Name field was removed
// (2026-08): it is what tells two finds on one page apart, and it is
// forwarded to Gramps as the citation note. Two finds with the same page AND
// the same comment would be indistinguishable data, so saving deduplicates
// on exactly that pair. Everything about the page itself lives in
// StoredPage, referenced by PageId.
record Finding(Guid Id, Guid PageId, DateTime SavedAt, string Comment);
