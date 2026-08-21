# Matrikelhelfer — Architecture Overview (WPF app)

> This document covers the WPF desktop app (`Matrikelhelfer/`) only. A browser-extension reimplementation of the citation-display idea, including Ancestry citation filling, lives in `extension/` — see `docs/ARCHITECTURE_EXTENSION.md`. The WPF app previously had its own UI-Automation-based Ancestry-fill feature; it was removed as chronically unreliable (2026-07, in the pre-publication "rctest" repo this project started as) in favor of the browser extension, and this app now focuses solely on church-book citation tracking/display.
>
> A third component, the **Gramps bridge** (`gramps-addon/MatrikelHelferBridge/`, Python), lets the app write finds directly into a running Gramps Desktop instance over a local HTTP/JSON API served by a Gramps addon. Spec: `docs/MatrikelHelfer-Gramps-Bridge-Anforderungen.md`; component overview and headless test suite: `gramps-addon/MatrikelHelferBridge/README.md` and `gramps-addon/tests/`. The stage-0 feasibility spike is preserved in `spike/BridgeSpike/`. The C# side: `GrampsBridgeClient/` (reusable client library — discovery, DTOs, async HTTP client) and `GrampsBridgeTester/` (WPF sandbox that prototyped the "Gramps-Modus" UI from spec §7.3; now **frozen** — it stays the bridge/API exercise tool, all further UI work happens in the app). Integration into `Matrikelhelfer/` is **done through stage 3** — the Gramps-Modus view is ported and functional in the app's Gramps tab (see "Gramps-Modus" section below); remaining is stage 4 (Upload-Vermerk on the Finding, read-back polish, change-list persistence). Gramps is the first genealogy backend — extensibility to others is planned via the same bridge protocol or a client-side adapter (see spec §1), which is why **all backend calls are confined to one adapter class**: `Services/GrampsBackend.cs` (discovery → liveness check → ping, api-version guard, session-id tracking, plus thin pass-throughs for search/detail/event-types/capture-batch; the app references `GrampsBridgeClient/` but ViewModels never touch `BridgeClient` directly). The Gramps tab **auto-connects when opened** (cheap local ping); connection state is a compact clickable indicator in the status bar.

## Purpose
A Windows WPF desktop app (.NET 8, C# 12, MahApps.Metro, German UI) for genealogy research on online church-record scans — originally `data.matricula-online.eu`, now multiple providers behind `ICitationExtractor` (Matricula + DFG-viewer archives). The user connects the app to a running browser, clicks **Lesen** on a Matricula scan page, and gets the citation metadata (Land, Bistum, Pfarrei, Buchtyp, date range, Signatur, scan/page, links) in an always-on-top window — formatted as a **Quellenangabe** (the church book = the genealogy software's *source* record) and a **Zitatangabe** (the specific page = a *citation* on that source), both in user-definable formats and copyable per click. Finds can be annotated (Kommentar) and saved persistently for later revisiting.

## Project Structure

```
Matrikelhelfer/
├── App.xaml/.cs                 # StartupUri -> Views/MainWindow.xaml
├── Models/
│   ├── MatriculaInfo.cs         # record: raw scraped citation fields (+ stored PageUrl; computed CitationTitle/BookLabel/BookUrl/BookKey/EffectivePageUrl, [JsonIgnore])
│   ├── CitationStyle.cs         # record: named format template (Name + Template string)
│   ├── StoredPage.cs            # record: one church-book page (Id, Info incl. the user-edited Seite) + page identity
│   ├── Finding.cs               # record: one find on a page (Id, PageId, SavedAt, Comment — the Name field was removed 2026-08)
│   ├── LibraryEntry.cs          # record: Finding joined to its StoredPage (what the exporters take)
│   └── SavedRecord.cs           # record: LEGACY flat find - read only by the entries.json migration
├── Browsers/                    # Per-browser address-bar lookup strategies
│   ├── IBrowserAddressBarLocator.cs
│   ├── AddressBarLocatorBase.cs # shared URL-shape fallback scan
│   ├── ChromiumAddressBarLocator.cs  # Chrome/Edge/Brave/Opera/Vivaldi/IE
│   └── FirefoxAddressBarLocator.cs
├── Services/
│   ├── BrowserConnection.cs     # manual browser bridge: list/pick browser, read URL+title, find page link, drive navigation
│   ├── ICitationExtractor.cs    # per-provider extraction strategy (CanHandle by URL) + PageContext
│   ├── MatriculaExtractor.cs    # Matricula provider: HTTP fetch + HTML parse + cache
│   ├── DfgViewerExtractor.cs    # DFG-viewer (tx_dlf) provider: METS/MODS XML parse + cache
│   ├── ArchionExtractor.cs      # ARCHION provider: parses the (login-gated) browser tab TITLE, no fetch
│   ├── NativeInput.cs           # SendInput-based keyboard simulation (Ctrl+L / Enter re-navigation)
│   ├── CitationTemplateEngine.cs # {Placeholder} template rendering over a MatriculaInfo
│   ├── CitationStyleCatalog.cs  # built-in templates: first-start seeds + fixed defaults
│   ├── FormatSettingsStore.cs   # persists format lists + active selections + the global Gramps citation confidence (formats.json, best-effort)
│   ├── LibraryStore.cs          # persists pages+findings (library.json v3, atomic; migrates pre-2.0 entries.json; folds the v2 Name field into the comment)
│   ├── EntryCsvExporter.cs      # exports saved finds as German-Excel-friendly CSV
│   ├── BibTexExporter.cs        # exports saved finds as a BibTeX .bib source library (one @misc per unique book)
│   ├── GrampsBackend.cs         # THE adapter for all Gramps-bridge calls (connect/ping/search/detail/event-types/capture-batch)
│   └── TreeGraph.cs             # local person/family graph (ported from the tester; virtual + real persons are the same node type)
├── ViewModels/
│   ├── MainViewModel.cs         # owns connection+extractor, all main-window state
│   ├── GrampsViewModel.cs       # the Gramps-Modus tab: tree/link view/change list/batch upload
│   ├── GrampsModels.cs          # its item VMs (PersonBoxVM, FactRowVM, SourceCardVM, GrampsChangeEntry, ...)
│   ├── FormatEditorViewModel.cs # backs the settings format editor (+ FormatItem, FormatTargetViewModel)
│   ├── RelayCommand.cs          # minimal ICommand (no MVVM toolkit dependency)
│   └── SavedEntry.cs            # joins a Finding to its StoredPage for display (INPC, in-place updates; the shared Card* card face)
└── Views/
    ├── MainWindow.xaml/.cs      # main UI: tabs (Zitate | Gramps) + "Ablage" card tray
    ├── GrampsModeView.xaml/.cs  # the Gramps tab (tree, event<->source link view, change list; connector-line drawing + tray drop)
    ├── EventTypeDialog.xaml/.cs # grouped Gramps event-type picker + event date (qualifiers) + place + description; also edits pending events
    ├── NewPersonDialog.xaml/.cs # name/gender entry for a virtual person from a "Neu" slot; also edits virtual persons
    ├── CitationEditDialog.xaml/.cs # edits a saved find's citation fields (Seite + Notiz) incl. "Als Kopie speichern"
    ├── ChangeListDialog.xaml/.cs # the Änderungsliste as its own resizable window (the tab keeps a summary row)
    ├── SettingsWindow.xaml/.cs  # format editor dialog
    ├── BrowserPickerWindow.xaml/.cs  # "connect to which browser?" chooser
    ├── ChoiceWindow.xaml/.cs     # reusable 3-way question (primary / secondary / real Cancel)
    ├── CopyableField.xaml/.cs   # reusable labeled field w/ copy button (opt-in wrap/edit/warn)
    └── CitationPreviewConverter.cs   # IMultiValueConverter: renders a style against DisplayedInfo
```

## Key Design Patterns

### MVVM
`MainWindow` binds to `MainViewModel` (constructed in code-behind); `SettingsWindow` to `FormatEditorViewModel`. Code-behind holds only pure view logic (caret insertion for placeholder chips, DataGrid double-click/right-click glue, initial sort description). Commands are hand-rolled `RelayCommand`s (CanExecute via `CommandManager.RequerySuggested`; background state changes need an explicit `CommandManager.InvalidateRequerySuggested()`).

**Accepted deviation**: `MainViewModel` opens dialogs directly (`BrowserPickerWindow`, `SettingsWindow`, `MessageBox`, `SaveFileDialog`) instead of going through an injected dialog-service abstraction. Deliberate for a single-window app with no automated UI tests — a service interface with exactly one implementation would be ceremony without benefit. Revisit only if ViewModel-level tests ever need to fake dialogs.

`CopyableField` is the reusable building block: optional leading icon + floating-watermark label + value + copy button (+ optional extra-action button, e.g. open URL / save image). Opt-in DPs: `IconKind` (a `PackIconFontAwesome6Kind` shown left of the label in a muted `Gray3`; default `None` collapses the icon column so icon-less fields lay out unchanged), `ValueWrapping` (long rendered text), `IsValueReadOnly=False` (editable fields also need `Mode=TwoWay` on their Value binding), `WarnWhenEmpty` (red border while empty — applied from code-behind as a **local** `BorderBrush` value because MahApps' own style/template triggers silently override a mere style-trigger setter).

### Manual browser connection (`Services/BrowserConnection`)
Replaced the earlier `BrowserWatcher` (WinEventHook + UIA property-changed events): the always-on hooks proved fragile, so the app is now **on-demand**. The plug button connects **directly when exactly one browser is running** (the normal case); `BrowserPickerWindow` (running browsers via `EnumWindows`, one visible top-level window per PID, filtered by the address-bar locators, with executable icons via System.Drawing) only appears when there is an actual choice, and zero running browsers just yields a status hint. Connecting stores the target. **Lesen** then reads the address bar's UIA `ValuePattern` at that moment; no polling, no event subscriptions. If the browser process has exited, the connection is dropped and the UI reflects it.

**`TryNavigateAsync`** drives the connected browser to a URL (used by "Im Browser öffnen" / double-click on a saved entry): `SetForegroundWindow` + **poll** `GetForegroundWindow()` (activation is asynchronous — a fixed delay would be a race), then `NativeInput.SendChord(Ctrl, L)` to focus the omnibox the way a user would (`AutomationElement.SetFocus()` is unreliable on Chromium's custom-rendered omnibox), `ValuePattern.SetValue()` for the URL (works without real OS focus), and `SendKey(Enter)` to submit (needs the focus Ctrl+L got).

### Per-browser address-bar lookup (`Browsers/`)
`IBrowserAddressBarLocator.FindAddressBar(window)` encapsulates one browser family's UI Automation quirks. `AddressBarLocatorBase` implements the shared fallback (scan all controls of the declared `TypeCondition` for one whose value is URL-shaped) so concrete classes only declare `ProcessNames`, `TypeCondition`, and `PreciseCondition`:
- **`ChromiumAddressBarLocator`** (chrome/msedge/brave/opera/vivaldi/iexplore): omnibox is `ControlType.Edit`, matched by `Name` (English only; non-English UI falls through to the URL-shape fallback).
- **`FirefoxAddressBarLocator`** (firefox): urlbar is `ControlType.ComboBox` with a locale-dependent `Name` but a **stable `AutomationId` = `"urlbar-input"`** — matched by AutomationId first.

Adding a browser = one new class plus one registration line.

### Provider extractors (`Services/ICitationExtractor`)
Multiple church-book providers are supported through one strategy interface, mirroring the per-browser locator pattern: every provider yields the same `MatriculaInfo` shape and differs only in which site it understands and how the fields are scraped. `MainViewModel` holds the extractor list and routes by URL: `CanHandle(Uri)` (host check) picks the provider, then `GetInfoAsync(PageContext)` scrapes; `DownloadImageAsync` is also per-provider (image fetches may need provider-specific headers) and is routed via the *displayed* record's stored page URL, so saved entries from any provider download correctly. Adding a provider = one new `ICitationExtractor` class plus one entry in `MainViewModel._extractors`.

`GetInfoAsync` takes a **`PageContext`**, not a bare URL, because not every provider's data lives in fetchable HTML. It bundles: the address-bar `Url`; the browser tab `Title` (read via `BrowserConnection.ReadActiveTabTitle` — a Win32 `GetWindowText`); and `FindLink`, an on-demand lookup that walks the rendered page's UI-Automation tree for a link matching a provider-supplied regex (`BrowserConnection.FindPageLinkMatching`). Matricula/DFG use only `Url`; ARCHION needs the other two. `FindLink` is deliberately lazy (the a11y-tree scan is slow and fragile) so only the provider that needs it pays for it.

**Address-bar display quirks**: browsers show URLs scheme-elided (no `https://`) *and* percent-decoded. `MainViewModel.TryParseBrowserUrl` normalizes before any extractor sees the URL: parse as-is and accept only an http(s) scheme, else prepend `https://` and re-parse. A naive `Contains("://")` scheme check is wrong — a scheme-elided DFG-viewer URL still contains a nested `://` inside its decoded `tx_dlf[id]` value (this exact bug shipped once).

### DFG-viewer extraction (`Services/DfgViewerExtractor`)
For church books shown through the DFG viewer (TYPO3/Kitodo `tx_dlf` frontend, e.g. dfg-viewer.de displaying the Erzbistum München digital archive). Matched by **query shape, not host** (`tx_dlf[id]=` present — any viewer instance carries the METS reference the same way; query keys are unescaped first, since browsers deliver `tx_dlf[id]` either raw or as `tx_dlf%5Bid%5D`). The viewer page itself is never fetched — everything comes from the **METS XML** that `tx_dlf[id]` points to, parsed with `XDocument` (no new dependency):

| Field | Source |
|---|---|
| `Pfarrei` | `mods:identifier[@type="Bestand-Name"]` (Actapro-specific identifier) |
| `Buchtyp` | top-level `mods:titleInfo/title` — must be the **direct child** of `mods:mods`, not the one nested in `relatedItem[@type=host]` (that's the parent holding, "Bestand: …") |
| `DatumVon/Bis` | `mods:originInfo/dateCreated[@point="start"/"end"]` ("01.01.1846" — trailing 4-digit year, so `ExtractYear` works unchanged) |
| `Signatur` | `mods:identifier[@type="VE-Signatur"]`, fallback `shelfLocator` — the combined value ("CB481, M7658"). Split: `SignaturPfarrei` = `identifier[@type="Bestand-Signatur"]` (the parish holding, "CB481"), `SignaturBuch` = the combined value minus that prefix ("M7658"). Matricula has no parish-level signature, so there `SignaturBuch` = `Signatur` and `SignaturPfarrei` stays empty. |
| `Land` / `Bistum` | derived from `dv:owner` via a small owner→(Land, Bistum) lookup table (the METS has no country field, and the owner names the *archive*, e.g. "Archiv des Erzbistums München und Freising", not the diocese). Unknown owners fall back to Land empty / owner verbatim — extend the table as archives are encountered. |
| `Scan` | `tx_dlf[page]` query parameter (mirrors Matricula's `?pg=`) |
| `ImageUrl` | `fileGrp[@USE="DEFAULT"]` → **direct** per-page JPG URLs (no obfuscation), joined to the page sequence via the physical `structMap`'s `ORDER`/`FILEID` |
| `ScanLabel` | `structMap` div `ORDERLABEL`/`LABEL` when an archive provides them; the München archive doesn't maintain Matricula-style page IDs, so it falls back to the **bare** scan number from the URL (keeps the Seiten-ID field, `{PageId}` and the image filename meaningful). Deliberately no "Scan" prefix — labels are raw data, prefixes are the formats' job. |

Parsed book data is cached in a **session-lifetime dictionary keyed by METS URL** — one fetch per book, ever: page-turning only changes `tx_dlf[page]` (an array lookup), and returning to an earlier book hits the dictionary (unlike Matricula's single-entry last-book cache). No eviction on purpose (a parsed book is ~100 KB); a failed *parse* is cached as null (broken METS won't heal — don't re-hammer the archive), while a failed *fetch* throws and is not cached (may be transient). Note `dfg-viewer.de` itself blocks non-browser HTTP clients (503) — irrelevant here since only the METS and image hosts are fetched, but don't add code that fetches the viewer page.

Related `MatriculaInfo.BookUrl` change: it no longer strips the whole query at `?` — that would delete the DFG viewer's METS reference. It removes only the page-position params (`pg`, `tx_dlf[page]`) plus TYPO3's `cHash` (computed over the full param set, stale once the page param is gone).

### Matricula extraction (`Services/MatriculaExtractor`)
`data.matricula-online.eu` pages are server-rendered — all citation text is in the initial HTML, so a background `HttpClient.GetAsync` + HtmlAgilityPack parse replaces any UI-Automation walk into the browser's DOM.

**Always reads the `/de/` version.** Matricula localizes both the field labels (`Buchtyp`/`Signatur`/`Datum von` vs English `Type`/`ID`/`Date from`) *and* the date values (`1. Januar 1872` vs `Jan. 1, 1872`); since this app is German, the extractor swaps the URL's first path segment (the language) to `de` before fetching, so the German-label lookups and the German date parser always match. The swap is done on the `GetLeftPart(Path)` **string**, not `AbsolutePath`, to preserve the double-encoding of a slash-bearing book id further along (`106%252F1872` — the site 404s on the single-encoded `%2F`). Only the *fetch* is forced to German; the stored `Url` (and thus every link) stays the page the user actually opened, so an English user keeps `/en/` links.

Field mapping (XPath against the fetched HTML):
| Field | Source |
|---|---|
| `Land` / `Bistum` / `Pfarrei` | Breadcrumb `<a href>` whose href matches the current URL path with 3/2/1 trailing segments stripped — looked up by href, **not** breadcrumb position. |
| `Buchtyp`, `Datum von/bis`, `Signatur` | `<tr><th>` label lookup against the site's own German field labels. |
| `Scan` (`pg`) | Parsed from the URL's `?pg=` query parameter — the current page is **not** in the server-rendered HTML (client-side viewer state only). |
| `ScanLabel` / `Page` | The viewer widget's `"labels"` JSON array (index-aligned: `labels[pg-1]`, e.g. `"Pollenfeld 01. 007"`). The surrounding `MatriculaDocView(...)` object literal is **not** valid JSON (bare-identifier values) — only the inner arrays are regex-extracted and JSON-parsed. `Page` (the trailing number) is parsed but **discarded at read time** (see "No page guessing" below). |
| `ImageUrl` | The widget's `"files"` array: `/image/<base64url>/` entries, decoded on demand for the one requested page (strip the `/image/` prefix **and** the trailing `/` before base64url-decoding — the slash otherwise corrupts the decode). |

**Book pages only**: a parish/diocese overview URL on the same host is rejected (null) by a **content** check — no viewer-widget `"labels"` and no `Signatur` table row means it's not a book page. Deliberately not a URL-depth check: how deep a country's hierarchy nests varies (e.g. Luxembourg).

**Two-tier caching**: doc + labels + files are keyed on the URL path only. Page-turning (`?pg=` changes) recomputes scan fields with no network call; a fetch only happens when the path (book) changes.

### ARCHION extraction (`Services/ArchionExtractor`)
For church books on **archion.de** (the German Protestant *Kirchenbuchportal*). Unlike Matricula/DFG, ARCHION is subscription-only and even the citation **metadata is login-gated**: a cookie-less HTTP fetch of a real (paid) book returns a stripped page with no breadcrumb (only the free sample books are public). This extractor therefore **does not fetch**. Instead it parses the **browser tab title** (`PageContext.Title`), which mirrors the logged-in page's `<title>` and carries the whole breadcrumb chain, reversed:

`Beerdigungsregister 1808-1840 | Bromskirchen | Dekanat Biedenkopf | Zentralarchiv … | Hessen: Kirchenbücher online mit ARCHION`

The site name `Kirchenbücher online mit ARCHION` is the anchor (everything before it is the chain; the browser's own `" - <Browser>"` suffix after it is discarded, and its absence means "not an ARCHION book page" → null). The chain is mapped **from the ends** — Buch = first, Pfarrei = second, Archiv = second-last, Land = last — so the varying middle depth (a *Dekanat* in Hessen, a *Kirchenkreis* in Thüringen, sometimes none) doesn't matter. The book label ("… 1808-1840") yields Buchtyp + the date range. `{Land}` here is ARCHION's top level (a Bundesland/Landeskirche region), **not** the country as with Matricula/DFG.

What ARCHION does *not* provide: no image URL (paywalled/session-bound — `DownloadImageAsync` throws, but is never reached since `ImageUrl` is empty), no archival signature, and no sequential scan number in the URL (`Scan = 0`; `{Scan-Nr}` and the list's `PageDescription` render empty at `Scan == 0` rather than "0"/"Scan 0"). The **page** is client-side viewer state, absent from the URL — so during normal browsing the address bar carries only the **book**, and `BookUrl` derives from it. The exact-page link is ARCHION's **permalink** (`archion.de/p/<code>`), shown only in the viewer's (user-opened) permalink panel and never in the URL; the extractor reads it via `PageContext.FindLink` (the UIA a11y-tree scan) and stores it in `MatriculaInfo.PageUrl`. `{PageUrl}`, the link field, and saved-entry re-navigation use `EffectivePageUrl` (= `PageUrl` if set, else `Url`), so a missing/closed panel degrades cleanly to the book URL. **This permalink read is experimental** — fragile (depends on the panel being open and on the browser exposing the link in its a11y tree) and comparatively slow (whole-window scan on the UI thread).

### Source/citation format system
Genealogy software keeps *sources* (the church book) separate from *citations* on them (the page) — the app's two central fields mirror that split:
- `CitationTemplateEngine.Render(template, info)` substitutes `{Token}` placeholders in one regex pass (over `{Name}`/`{Name:modifier}`), leaving unknown tokens and literal braces untouched — so a template can contain raw BibTeX braces (`title = {…}`). Placeholders (offered as chips in four topic rows — place / book / page / links+date): `Land, Bistum, Pfarrei, SignaturPfarrei` · `SignaturBuch, Signatur, Buchtyp, Von, Bis, JahrVon/Bis` · `Seite, Scan-ID, Scan-Nr` · `BookUrl, PageUrl, ImageUrl, AccessDate` (names match the main window's field labels). A token may carry a **modifier**: `{Token:clean}` renders a key-safe identifier (transliterate umlauts → strip other accents → runs of non-`[A-Za-z0-9-]` become `_`, hyphens kept; empty → `EMPTY`) — used for BibTeX cite keys but general. No legacy token aliases or migration paths — the app has no users besides its author; when tokens are renamed, locally saved `formats.json` templates are simply re-edited (or the file deleted to re-seed).
- **Seed BibTeX formats**: `CitationStyleCatalog` seeds a `BibTeX` source (`@misc` entry, cite key `{Pfarrei:clean}_{Signatur:clean}`, ISO `urldate`) and a `BibTeX (\cite)` citation referencing the same key.

**Per-format date style**: each `CitationStyle` carries a `DateFormat` id (persisted in formats.json; missing on old files → "original" via the record's constructor default). The editor's Datumsformat dropdown picks from `CitationTemplateEngine.DateStyles` (original / 22.03.1845 / 22. März 1845 / 22 MÄR 1845 GEDCOM / ISO / three English variants) and it applies to `{Von}`, `{Bis}` and `{AccessDate}` on render. Provider date strings are parsed with the known spellings (Matricula "1. Januar 1670", AEM "01.01.1846"); an unparseable date passes through **verbatim** — a citation must never invent a date. Gotcha: .NET's German `MMM` keeps "Juni"/"Juli"/"März" unabbreviated, so the GEDCOM style uses a fixed 3-letter month table. `{BookUrl}` is the URL without `?pg=` (identifies the book). `{Seite}` renders **empty** when no page is confirmed — deliberately no scan-number fallback (a citation must not pass a scan index off as a page).
- Users maintain **two format lists** (Quellen-/Zitatformate) in the settings dialog: a target dropdown switches the visible list; each list's selection is its *active* format. Editor pane = name + template + clickable placeholder chips (bare names, braces added on insertion at the caret; chips are `Focusable=False` so the TextBox keeps its caret) + live preview against a fixed `SampleInfo`. OK/Cancel dialog semantics (the VM edits a copy).
- The main window shows both fields as **ComboBoxes with rendered previews**: one shared `FormatPreviewTemplate` (format name + `CitationPreviewConverter` output against `DisplayedInfo`) serves the closed box (WPF's SelectionBoxItemTemplate behavior) and every popup entry. Selecting a format re-renders and persists immediately.
- `FormatSettingsStore` persists both lists + active selections to `%APPDATA%\Matrikelhelfer\formats.json` (best-effort: failures never take the app down). The same file also carries `GrampsConfidence` — the confidence for **all** citations the Gramps upload creates (settings-dialog dropdown; a GLOBAL setting on purpose, since every source here is a church book — outliers are regraded in Gramps). First-start seed: `CitationStyleCatalog.SourceSeed`/`CitationSeed` (user-curated: Sehr kurz / Kurz / Ausführlich / Chicago sources; Standard / Nur Seite / Kurz citations) with "Kurz" starting active in both lists (`Default*Name`); each list seeds independently if emptied.

### No page guessing (editable Seite)
The number in Matricula's scan label rarely matches the real handwritten page number, so the parsed value is discarded on every read: the Seite field starts **empty** (red-border warning via `WarnWhenEmpty`) and only ever holds what the user typed. An edit is pushed back into the displayed/current record (`info with { Page = ... }`) so `{Seite}` renders and saves with it; clearing it means "page unknown".

### Saved finds (persistent)

> The storage model itself is documented in **"Finds & pages"** below (Page/Finding split, identity, save flow, migration). This section covers the surrounding UI and export plumbing.

- `SavedRecord` (Models) is the **legacy** shape, kept only so `LibraryStore` can read a pre-2.0 `entries.json` during migration — nothing else uses it. Rendered citation text is never stored: display always re-renders with the *current* formats.
- `LibraryStore` persists to `%APPDATA%\Matrikelhelfer\library.json` (`version: 3`, pages + findings in one file). **Reframed 2026-08 (Gramps-Modus design):** the library is **short-term working storage** — finds collected across a few sessions until they are copied out / assigned and uploaded to the genealogy software — not a long-term research archive (the CSV/BibTeX exports are the archival artifacts). **Decision log: JSON over SQLite** — entries accrue by manual clicks (hundreds at most under the working-storage model), and a readable, backup-friendly file beats a binary DB. Writes remain **atomic** (temp file + `File.Replace`) and failures are **surfaced** in the status line (unlike the best-effort format store — losing a week of un-uploaded finds still hurts). A file that exists but won't parse sets `_storageUnreadable`, which **blocks saving entirely** — writing over a library we failed to read would destroy the data the failure is protecting.
- Main-window flow: the **Notiz** field (the "Notizen" section at the top; UI label renamed from Kommentar 2026-08, after the Gramps field it becomes — internally still `Comment`) annotates the current find and becomes the note on the uploaded Gramps citation. The former `Name` field was **removed 2026-08**: it never reached Gramps, and "whose record is this" belongs in the note. **Lesen always starts fresh**: every read attempt first clears ALL fields (a failed read must never leave the previous record standing next to its error message — it reads as success). The dirty check (`IsDirty`, see below) guards both **Lesen** and **selecting a saved entry** with a discard-confirmation dialog; rejecting the selection snaps the list highlight back via a `Dispatcher.BeginInvoke`d re-notification. Selecting an entry **binds** it (`_boundEntry`) and syncs `_currentInfo` to it, so an edited Notiz updates the *displayed* entry rather than saving against the last-read page. All actions are circle icon buttons in the top row (connect plug left; **Lesen + Speichern + tray toggle** right, so read and save sit together); **Lesen** is the primary action and gets a filled-accent circle to stand out. The fields column scrolls (`ScrollViewer`) and its sections (Notizen / Quelle / Zitat / Links) are grouped into subtle rounded Gray9 "cards" (`SectionCardStyle`); each field carries a leading `CopyableField` icon (the Quelle/Zitat dropdowns have none but are left-indented by the icon-column width to stay flush with the fields above). Status/error messages live in a one-line status bar at the window bottom (ellipsis-trimmed, full text in its tooltip — keep new status texts short). **Speichern** persists immediately and is enabled while there is something to commit (an unbound read, or a bound finding with uncommitted edits) — see the save flow below. Several finds per page are normal and expected.
- **Tabs (Gramps-Modus stage 1)**: the content column below the button row is a `TabControl` — **"Zitate"** (the classic fields view above) and **"Gramps"** (placeholder until the bridge connection and the ported Gramps-Modus view land). The tray (next bullet) sits OUTSIDE the tabs, so it serves both modes with one saving path.
- The saved finds show in the **"Ablage"** — a docked right-hand card tray (not a Flyout — a Flyout overlays and hides the fields; and no longer a DataGrid — with the working-storage model there is nothing to column-sort): a full-height Gray9 card holding the "Gespeicherte Einträge" heading and a `ListBox` of cards. The card content is the **shared card face** (`SavedEntry.CardTitle/CardSubtitle/CardPage` — parish / book type + years / Seite-or-Scan): ONE definition rendered identically by the tray and the Gramps tab's finding cards, so the two lists cannot drift apart visually; the tray adds the **comment's first line** on top (`CardNote`, bold, collapsed when empty — it is what tells two finds on one page apart), with the full comment as tooltip; fixed newest-first `SortDescription`, no date on the card. Each card also carries a hover **pencil** opening the shared citation-edit flow (see "Citation editing" below). Its top edge aligns with the Notizen card via a spacer bound to the REAL heights of the button row and the tab-header strip (a `TabItem` element *is* its header chip, so its `ActualHeight` is the strip height) — no magic pixel constants. Single click redisplays the entry in the main fields; **double-click** (or context menu) drives the browser back to the scan. Deleting is offered three ways, all persisted and all routed through one `RelayCommand<SavedEntry>` (`DeleteEntryCommand`): Entf/context menu act on the *selected* card (no command parameter), while a per-card **✕ button** — shown only while its card is hovered (`Visibility=Hidden`, so the layout stays put) — passes its own card as the parameter so it deletes without changing selection. Deleting the entry that's currently on display also clears the main fields (`ClearDisplay`), so its now-orphaned Notiz doesn't trip the discard-unsaved guard on the next selection. The card-bottom band holds the **BibTeX/CSV export buttons left** and the guarded **"Alle löschen"** (`ClearAllEntriesCommand`, OK/Cancel confirmation) right; the list scrolls inside the card.
- **CSV export** (button in the panel header → `EntryCsvExporter`): all raw fields (incl. the `SignaturPfarrei`/`SignaturBuch` split) plus the Quellen-/Zitatangabe *rendered with the currently active formats* — the stored data stays raw, but an export is a snapshot for outside the app (the user's own long-term archive, e.g. "did I work through this book years ago?"). Format targets German Excel: `;` separator and a UTF-8 BOM (without the BOM, Excel guesses ANSI and mangles umlauts on double-click); standard CSV quoting handles multi-line comments.
- **BibTeX export** (second panel-header button → `BibTexExporter`): writes a `.bib` **source library** — one `@misc` per unique *book* (finds are deduplicated by `BookKey`, since a `.bib` lists sources, not per-page citations), rendered with the user's `BibTeX` source format. If no `BibTeX` source format exists (user deleted it), a dialog offers to re-add the built-in catalog one or cancel. UTF-8 **without** BOM (LaTeX toolchains, unlike the Excel-targeted CSV).
  - **Append to an existing `.bib`** is the realistic workflow (a bibliography accrues over a career), so picking an existing file offers *Anhängen / Ersetzen / Abbrechen* (`SaveFileDialog.OverwritePrompt` is disabled — appending is a legitimate choice, not the mistake the generic prompt implies). Appending **never rewrites existing content**: it only adds at the end, so hand-written entries, other source types and edited cite keys survive untouched. That rules out updating a stale entry in place — accepted, since splicing a user's `.bib` risks mangling their formatting.
  - **"Already present" is decided by `url` first, cite key second.** `ParseEntries` splits the existing file at entry headers (any `@type`, skipping `@string`/`@comment`/`@preamble`) and reads each key plus any `url` field, normalizing it through `MatriculaInfo.NormalizeBookUrl` so both sides compare on the same footing. The URL wins because users are explicitly free to rename keys; where either side has no URL, a key match is treated as already present — which can't tell "same book" from "different book, same generated key" apart, the reason the URL check exists at all.
  - Cite keys are made unique (`a`/`b`/`c` suffixes) so the file compiles even when distinct books collide (ARCHION's signature-less `_EMPTY` keys). The suffix search is seeded with the **existing file's** keys, or an appended book could collide with an unrelated entry already in there.
  - Every run also appends a dated block to **`Matrikelhelfer_Export.log`** beside the `.bib` (fixed name — it says where the file came from): what was added, what was already present *and how it was recognized*, which keys had to be renamed, and warnings. The renamed-key line is the point of the log — a `\cite` in an existing `.tex` would otherwise silently miss, and finding that by hand in a large bibliography is expensive. Best-effort: a log that can't be written never fails the export (the `.bib` is the product), and the failure is reported in the status line.
- Panel geometry (`MainWindow.xaml.cs`, `UpdateEntriesPanel`): while OPEN the column roles invert — the fields column is frozen at its pixel width and the panel column is star-sized, so the toggle's window-width animation *and* manual window resizing only ever change the panel, never the fields; the resized tray width is remembered across toggles. The GridSplitter between fields and tray was **removed 2026-08** (with the Gramps tab's fixed 500px column there is nothing left to trade width between — resizing the window already sizes the tray), which also retired the drag-handle margin juggling: the gap between fields and tray is simply the fields grid's constant 15px right margin. The fields `ScrollViewer` adds right padding **only while its scrollbar is showing** (a trigger on `ComputedVerticalScrollBarVisibility`) — a constant padding would inset the cards but not the button row above, making the closed-window right gap wider than the left.

### Finds & pages

Replaces the flat `SavedRecord` model. Written before the code so the intent is on record: this is not a document editor's save/save-as, and reading it as one is what makes the behaviour look arbitrary.

#### The intent

The unit the user creates is a **find** — a person spotted on a scan — not a page. **Several finds on one page are normal and expected** (two baptisms in the same register opening), so "save" can never mean "one entry per page", and "save as" has no meaning at all: there is no document, only a growing list of finds that happen to cluster on pages.

The shipped model conflated the two. Each find stored the *entire* scrape (`MatriculaInfo`) inline, and the only duplicate guard compared that whole record plus both note fields for value equality. Since the record includes volatile fields — `Url` (with `cHash`, `?pg=`, `/de/` vs `/en/`), `PageUrl`, `ImageUrl`, `ScanLabel`, and the user-edited `Page` — practically any change made the record "new", so pressing Speichern after typing one more character appended a second row for the same person. That is the entry explosion; deduplication alone would not have fixed it, because the app had no notion of *which* saved find the display currently represents.

Two ideas fix it:

1. **Separate what the user edits from what is scraped.** The governing rule: *store what the user edits; derive what is scraped.* The handwritten page number (`Seite`) is user-edited and belongs to the **page**, so it is stored once there and every find on that page sees it. Country/archive/parish/book are pure scrape, re-derived correctly on every read, so they are **not** stored as entities — a five-level `Land → Bistum → Pfarrei → Buch → Seite` tree is built at *display* time by grouping. Storing that tree was considered and rejected: its nodes would have to be keyed on scraped display strings, and those are demonstrably unstable (the `/en/` bug produced different `Buchtyp`/`Signatur` text for the same book), so a name-keyed tree silently forks one parish into two branches. `BookKey` — a normalized URL — cannot fork that way. If a level ever becomes hand-editable (a corrected parish name, a note on a book), *then* it has earned entity status by the same argument `Seite` did.
2. **The display is explicitly *bound* to a find.** Editing a bound find updates it in place; a second find on a page only ever comes from a genuinely different comment or an explicit "Als Kopie speichern" — never as a side effect of typing.

#### Entities

Storage stays flat — two collections, no nesting.

```
Page                                   Finding
  Id                                     Id
  BookKey   (normalized, see below)      PageId → Page.Id
  Scan                                   SavedAt
  Seite     ← the only user-edited       Comment
  ScanLabel, Url, ImageUrl, PageUrl
  book metadata (Land, Bistum, Pfarrei,
  Buchtyp, Datum*, Signatur*)
```

> **2026-08: the `Name` field was removed from `Finding`.** It never reached Gramps (the upload sends only Seite, permalink and the comment-as-note), so it was purely a tray label — and the comment covers that. With it went the whole name-keyed prompt system this section originally specified; the flow below describes the current, promptless behaviour. Stored names were folded into the comment by the v3 migration (see below).

**Book is derived, not stored** — group Pages by `BookKey` at runtime, exactly as `BibTexExporter` already does. Book metadata is duplicated per Page and that is deliberate: it is never hand-edited, so it cannot drift the way `Seite` did.

#### Identity

`BookKey` = `BookUrl` normalized: host lowercased, `www.` dropped, `https` forced, the **language path segment neutralized** (`/de/` ≡ `/en/` — we deliberately keep links in the user's language, so raw `BookUrl` comparison would under-merge), trailing slash stripped, surviving query params sorted (DFG's `tx_dlf[id]` must survive). `Url`/`PageUrl` are untouched — `BookKey` exists only for comparison and is never displayed or linked.

**Page discriminator**, one rule with fallbacks:

> `Scan` when `> 0`; else `Seite` when non-empty (trimmed); else **none** → every read makes a new Page.

Matricula/DFG take the first branch. ARCHION has no scan number (`Scan == 0`) and its permalink encodes zoom/pan, so it is a *view* URL, not a page identifier — the user-entered `Seite` is the only page-level discriminator available, and a find with no page reference cannot be cited anyway. `CopyableField.WarnWhenEmpty` should flag `Seite` on ARCHION reads for that reason.

**Consequence — mutable identity on ARCHION.** Correcting `Seite` there changes which page the object *is*. If no Page holds the new number it is a harmless rename; if one does, the two must be **merged** (move findings over, delete the emptied Page). Merge silently with a status line (*"Seite 43 mit vorhandener Seite zusammengeführt – 2 Funde"*) — once the numbers match, "same page" is unambiguous.

#### Flow

`Lesen` **never writes.** Page changes commit on `Speichern` only; an unsaved `Seite` correction is discarded, guarded by the dirty check.

| Trigger | State | Result |
|---|---|---|
| **Lesen** | any | Load page context, unbind. No storage write. |
| Select entry in list | any | Display + **bind** to that Finding (same semantics as a read) |
| **Speichern** | nothing bound, a Finding with the **same comment** exists on the Page | Just bind to it ("Bereits gespeichert") — no write, no duplicate |
| **Speichern** | nothing bound, otherwise | Create Finding (+ Page if new), bind |
| **Speichern** | bound | Update bound Finding in place |
| Comment / Seite edit | any | Lands on the bound Finding / its Page. Never prompts. |
| Delete last Finding on a Page | — | Garbage-collect the orphan Page |

**The save flow is promptless** (since the Name removal). The comment is a find's whole payload, so *same page + same comment* is indistinguishable data that would upload as identical Gramps citations — deduplicating on exactly that pair is correct, not lossy, and unlike the pre-2.0 value-equality guard it compares nothing volatile. Distinct records on one page = distinct comments; the deliberate second-citation case ("same page, different note for another person") is served by **"Als Kopie speichern"** in the citation dialog, not by a prompt. (The original name-keyed design with its `ChoiceWindow` prompts lives in the git history; `ChoiceWindow` itself survives — the BibTeX append/replace question still uses it.)

**Citation editing** (`MainViewModel.EditCitation` → `CitationEditDialog`) is the ONE flow for editing a saved find's citation fields — **Seite** (a page fact: commits to the shared page, identity merge included) and **Notiz** (the future Gramps citation note) — reachable from the tray card's hover pencil/context menu *and* from the Gramps tab's finding cards (routed through a delegate; the returned entry lets the Gramps side auto-adopt a copy). **"Als Kopie speichern"** creates a second Finding on the same page with its own comment (the same dedupe rule applies); the original keeps its comment. Editing bypasses the Zitate tab's binding entirely, so after a commit the flow resyncs the displayed fields when the edited entry happens to be bound.

**Dirty check** (drives the discard-confirmation on **Lesen** *and* on switching list selection): the annotation state (Kommentar/Seite) differs from its committed counterpart — the bound Finding and its Page, or *empty* when nothing is bound. Deliberately binding-independent: the case that matters most is a fresh read with a typed-but-never-saved comment, where nothing is bound at all.

#### Accepted limitations

- **ARCHION without a page number** has no discriminator, so re-reading the same physical page yields a second Page object and a `Seite` correction on one will not reach the other.
- **Handwritten numbers are not guaranteed unique within a book** — registers that restart numbering per section can make two physical pages both read "42", which would wrongly merge. Contained to ARCHION, and strictly better than no identity. `Seite` is trimmed but otherwise taken verbatim: `42a`, `42 recto` and `42` are distinct pages. No clever parsing.

#### Migration (one-time, at startup)

`entries.json` already carries a `version` field — key off it. The old flat records convert cleanly: findings map **1:1**, so the visible list keeps exactly the same rows and count; only Pages consolidate underneath. Nothing appears to vanish.

- **One file, not two.** `library.json` = `{ "version": 3, "pages": [...], "findings": [...] }`, written atomically (temp + `File.Replace`). Two files would leave a window where findings are orphaned from their pages.
- **v2 → v3 (Name removal, 2026-08)**: `LibraryStore` serializes findings through a `StoredFinding` DTO that still reads the v2 `Name`; on load a non-empty name is **folded into the comment as its first line** (names were real user data — dropping them silently is not acceptable, and the comment is where such information now lives; folded names consequently reach Gramps as part of the citation note). The v1 conversion folds the same way. No backup ceremony — the fold is lossless and only materializes on the next save.
- **Order: write new → read it back and verify → only then rename the old.**
- **Rename, never delete** — `entries.json` → `entries-v1.bak`. A conversion bug (a field that quietly arrives empty) often surfaces days later, and this file can represent years of research. A few KB of backup is the cheapest insurance in the app.
- **Conflicting values when several old records collapse onto one Page** — they can disagree precisely because the old model let them drift (`Seite` corrected on one but not another; `/de/` vs `/en/` `Url`s; differing `ImageUrl`/`PageUrl`/`ScanLabel`). Rule: **most recent `SavedAt` wins, but a non-empty value beats an empty one** — so a later correction beats an earlier blank, and a blank never overwrites real data. Without an explicit rule this is whatever order the loop happened to run in, which is how a correction gets silently lost.
- **A corrupt or unreadable `entries.json` must fail loudly** and leave the file alone — treating it as "nothing to migrate" would start empty and then write a fresh library over the top.
- Old ARCHION records take the `Seite` branch, so those *with* a page number merge into shared Pages and those without stay separate. Desired, but worth knowing it happens on first launch.

> Note: this invalidates the "no users besides the author, so no migration code" convention (see *Conventions*, and `CLAUDE.md`) — 1.0.0 shipped and there are real `entries.json` files in the wild.

### Gramps-Modus (the Gramps tab)

The full design history and semantics live in the spec (`docs/MatrikelHelfer-Gramps-Bridge-Anforderungen.md`, §7.3) — this is the app-side summary. The tab is a **fixed-width 500px column** (search / walkable tree / Fakten|Quellen link view taking all remaining height / a one-line change-list summary row; a wider window adds whitespace, a narrower one clips) so the app stays usable beside a browser on one screen.

- **Graph** (`Services/TreeGraph.cs`): loaded and newly created persons are the same node type, with the family object between persons (Gramps'/GEDCOM's model). Server detail fetches upsert nodes in place; clicking any box — real or virtual — centers it via the same code path. Person boxes use **smart name shortening** (middle givens → initials → first-given initial; the surname survives, full name in the tooltip) and compact two-line boxes (name + `*1800`/`+1880`/`1800–1880`); the couple keeps full life lines. Children page via chevrons (visible only on overflow).
- **Finds come from the Ablage**: a tray card is *adopted* to the centered person (drag onto the Quellen panel, double-click, or context menu) — pure session-local staging (`TreePerson.AdoptedFindings`); only assignments write anything. Adopted cards and the person's existing citations share the Ancestry-style link view (click = connector lines, double-click = assign mode). Finding cards render the same **shared card face** as the tray (`SavedEntry.Card*`) and carry a hover **pencil** (the shared citation-edit flow — a copy made here is auto-adopted to the centered person) and a hover **✕ = un-adopt** (`UnadoptFinding`): removes the card from THIS person's working set only — the find stays in the tray, unlike the tray's delete-✕; staged assignments of the citation to this person's events are removed with it after a confirmation (they would otherwise upload invisibly), while citation references baked into pending events stay. Existing Gramps citation cards have neither control (the bridge has no update API).
- **Pending items are editable until upload** (real Gramps objects stay read-only — no update API by design): the CENTERED virtual person's large box shows hover pencil/✕; every virtual box and every "(neu)" event row additionally has a Bearbeiten/Entfernen context menu (a `ContextMenu` popup is outside the visual tree, so its commands route through the placement target's `Tag` = the view model). Editing reuses `NewPersonDialog`/`EventTypeDialog` in edit mode; the event-type list is filtered to the entry's scope (person↔family would change the owner that assignments hang off — that case is delete + re-create). Edits mutate the change entry in place (the entry `Id` is load-bearing: `DependsOnId`, graph node ids and fact-row ids reference it) and ripple: a renamed person updates the graph node plus the `EntityLabel` of all dependent entries; an edited event re-derives the `TargetLabel`/`FindLabel` snapshots on attach entries (display only — the upload payload always resolves fresh). The box/row ✕ routes through the same cascade `DeleteEntry` as the change list. Pending state shows only as the accent tint — the former "○n" corner badges were removed (details live in the change list).
- **Change entries reference the Finding by id** (not a field snapshot): the source/citation payload (title/`MH_SourceKey` slug/Abkürzung = `CitationTitle`, Signatur, Seite, permalink, Notiz as note, repository per provider, confidence from the global setting) is resolved from the *current* library state at upload time, so a later Seite correction still reaches Gramps; a deleted Finding blocks the upload with a clear message. New events carry type, date (with qualifier), **place** (by NAME — preset with the active find's parish; the bridge reuses an existing Gramps place casefolded or creates a bare one in the same transaction) and description; they take the active finding card's citation, or go **citation-less** when none is active.
- **The Änderungsliste lives in `ChangeListDialog`** (2026-08): the tab spends only a summary row on it ("n Änderungen" + Details… + the send button), the resizable dialog shares the `GrampsViewModel` as DataContext — so its tree, cascade deletes and send command are the SAME objects and the list stays live while open. The delete buttons stay in the dialog deliberately: it is the only GLOBAL view (in-place removal requires centering each owner), group delete has no in-place equivalent, and the blocked-upload recovery ("betroffene Änderungen bitte entfernen") happens there.
- **Upload** = one `capture-batch` (one Gramps transaction, one undo); returned handles replace temp ids in the graph nodes in place, then the displayed slice is re-read. A Gramps tree switch (session-id change) clears the graph.
- Stage 4 (open): Upload-Vermerk on the Finding (✓ badge in the Ablage), change-list persistence for interrupted sessions (the graph/entries are flat and id-referenced by design).

## Data Flow

1. Plug button → `BrowserPickerWindow` → `BrowserConnection.Connect` (stores the chosen browser window).
2. **Lesen** → `BrowserConnection.ReadActiveTabUrl()` (UIA ValuePattern) → URL routed to the matching `ICitationExtractor` (`CanHandle`) → `GetInfoAsync(uri)` → `DisplayInfo` populates all fields and renders Quellen-/Zitatangabe with the active formats; Kommentar/Seite reset.
3. **Speichern** (promptless) → `CommitPage` (create, update, or merge the `StoredPage`) → bind to a same-comment `Finding` on the page, else create, else update the bound one → `LibraryStore.Save` (atomic).
4. Selecting a saved entry → **binds** it → `DisplayInfo(entry.Info)` + restore Kommentar; double-click additionally → `TryNavigateAsync(entry.Info.EffectivePageUrl)`.
5. Settings dialog OK → new `FormatSettings` → both dropdowns refresh, fields re-render, the Gramps citation confidence flows to `GrampsViewModel`, `FormatSettingsStore.Save`.

## Known Gotchas (accepted, worth remembering)

- **Build fails with `MSB3027`/file-lock if a previous run is still open** — `taskkill /F /IM Matrikelhelfer.exe` first. Variant: the lock holder can also be a **Visual Studio debug session** ("Visual Studio Debug Adapter", app as `.NET Host`) — don't kill that blindly; let the user stop debugging.
- **`OutputType` must be `WinExe`**, not `Exe` (console window otherwise).
- **UIPI / elevation mismatch is a silent failure mode** for UI Automation across privilege levels.
- **Don't `JsonSerializer.Deserialize` the whole `MatriculaDocView(...)` argument** — JS object literal, not JSON; only the inner `"labels"`/`"files"` arrays parse.
- **The `?pg=` parameter is not reflected in Matricula's server-rendered HTML.**
- **`SetForegroundWindow` is asynchronous** — poll `GetForegroundWindow()`, never use a fixed delay.
- **`AutomationElement.SetFocus()` is unreliable on Chromium's omnibox** — use the browser's own Ctrl+L via `SendInput`.
- **MahApps style/template triggers override style-trigger setters** on things like `BorderBrush` — set a **local value** from code-behind when a visual state must win (see `CopyableField.UpdateWarnBorder`).
- **WPF pushes a transient `null` through two-way `SelectedItem` bindings while `ItemsSource` is swapped** — the format-selection setters ignore `null` and the new selection is re-applied afterwards by record value-equality.
- **Get-only record properties serialize but never deserialize** (System.Text.Json) — computed props on `MatriculaInfo` carry `[JsonIgnore]` so `entries.json` doesn't accumulate misleading derived values.
- **Clicking an already-selected ListBox/ComboBox item fires no selection change** — if a click must always have an effect, handle the mouse event too (see the format editor history) or structure the UI so selection is sufficient.
- **DataGrid right-click does not select the row underneath** — without `PreviewMouseRightButtonDown` selection glue, a context menu acts on the previously selected row.
- **Nested XML comments are illegal** — XAML blocks containing comments can't be commented out wholesale; use `Visibility="Collapsed"` to park them.
- **A WPF animation holds its property forever** — after animating `Window.Width` (or a column), release it (`BeginAnimation(prop, null)`) and re-set the final value locally, or manual resizing/splitter drags are silently blocked. Related: a GridSplitter drag pins *both* neighbor columns to fixed pixel widths — restore the intended star-sized column afterwards.

## External Dependencies
- **HtmlAgilityPack** — HTML parsing for Matricula pages.
- **MahApps.Metro** (+ **IconPacks.FontAwesome6**) — window chrome, styles, Flyout, icons.
- **System.Drawing.Common** — browser executable icons in the picker.
- **System.Windows.Automation** (via `UseWPF`) — address-bar read/write.
- **user32.dll** P/Invoke (`EnumWindows`, `GetWindowThreadProcessId`, `SetForegroundWindow`, `GetForegroundWindow`, `SendInput`, …) — see `BrowserConnection`/`NativeInput`.

## Conventions
- C# 12 / .NET 8, nullable reference types enabled.
- MVVM, no business logic in code-behind.
- German UI text; German field names double as template placeholder names.
- This is a personal test/PoC tool — targeted fixes over speculative resilience (no defense against Matricula site redesigns, no support for untested browsers) unless asked.
- **Migration policy differs per file, by what losing it costs.** `formats.json` stays migration-free: a lost format is re-picked in a minute, so renamed tokens simply get re-edited (or the file deleted to re-seed). `entries.json` / `library.json` **does** get migration code — 1.0.0 shipped and the files exist on real users' machines; even as short-term working storage, silently losing someone's collected-but-not-yet-transferred finds is not acceptable. The blanket "no users yet, so no migration" rule that predates the release no longer applies to saved finds.
