using System;

namespace Matrikelhelfer.Models;

// One saved find, as persisted: the scraped citation data plus the user's
// own notes and bookkeeping fields (Id/SavedAt) so entries stay addressable
// and sortable however the UI later presents them (tree, flat by date, ...).
// Deliberately flat storage - hierarchy (Land -> Bistum -> Pfarrei -> Buch)
// is derived from Info at display time, never stored.
record SavedRecord(Guid Id, DateTime SavedAt, string Name, string Comment, MatriculaInfo Info);
