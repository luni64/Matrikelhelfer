# Next Release

## Features

- Selecting a saved entry now also navigates the tracked browser tab back to that entry's URL.
- Main window fields are now grouped under "Quelle", "Zitat" and "Links" section headings for clearer visual structure.
- Main window UI text translated to German.
- Multi-provider support: besides Matricula, church books shown through the DFG-Viewer (`tx_dlf`) are now readable — first archive: the Digitales Archiv des Erzbistums München und Freising (METS/MODS-based extraction, direct image links, one METS fetch per book per session). The provider is picked automatically from the browser URL.
- Signature split: new `SignaturPfarrei` (parish holding, e.g. "CB481") and `SignaturBuch` (individual book, e.g. "M7658") fields/placeholders alongside the combined `Signatur`; Matricula fills only the book part.
- CSV export of the saved entries (button in the panel header): all raw fields plus the Quellen-/Zitatangabe rendered with the active formats; German-Excel-friendly (`;`-separated, UTF-8 BOM).
- Format editor rework: placeholder pills grouped into topic rows with explanatory tooltips; tokens renamed to match the field labels (`Von`, `Bis`, `Scan-Nr`, `Scan-ID`, `PageUrl`, single `AccessDate`); new per-format date-format dropdown (numeric/long/GEDCOM/ISO German + English variants) applied to `{Von}`, `{Bis}` and `{AccessDate}`.
- Main window rework: Lesen and Speichern are now circle icon buttons (next to connect resp. the saved-entries toggle), settings moved to the title bar, Name+Kommentar united under a "Notizen" heading, status/error messages in a one-line status bar at the bottom, fields column scrolls vertically, format dropdowns labeled "Quelle (…)"/"Zitat (…)".
- Reading a new page warns before discarding unsaved input (Name/Kommentar/Seite) — both on Lesen and when selecting a saved entry.
- Reworked built-in format seed (fresh installs): sources Sehr kurz / Kurz / Ausführlich / Chicago, citations Standard / Nur Seite / Kurz, with "Kurz" starting active in both lists.

## Bug Fixes
- Feldbeschriftung „Link auf die Matrikula Seite" korrigiert zu „Link auf die Kirchenbuch-Seite" (Tippfehler + anbieterneutral); Text im Browser-Auswahlfenster ebenfalls anbieterneutral formuliert.
- A failed read (unsupported page, error) now clears the previously displayed record instead of leaving it standing next to the error message.
- Matricula parish/diocese overview pages are rejected ("Keine unterstützte Kirchenbuch-Seite") instead of displaying empty fields.
- Address-bar URLs displayed scheme-elided AND percent-decoded (e.g. DFG-Viewer URLs with a nested `https://` in a query value) are now parsed correctly.
- `{BookUrl}` no longer strips the whole query string (which destroyed the DFG-Viewer's METS reference) — only the page-position parameters and TYPO3's `cHash`.
- The status bar clears the "Nicht verbunden" hint after successfully connecting.
- Editing and saving notes on a redisplayed saved entry now saves against the displayed entry, not the last-read page.
- The save-duplicate check is cached instead of rescanning all entries on every UI event.
