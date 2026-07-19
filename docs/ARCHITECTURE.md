# Matrikelhelfer — Architecture Overview (WPF app)

> This document covers the WPF desktop app (`Matrikelhelfer/`) only. A browser-extension reimplementation of the citation-display idea, including Ancestry citation filling, lives in `extension/` — see `docs/ARCHITECTURE_EXTENSION.md`. The WPF app previously had its own UI-Automation-based Ancestry-fill feature; it was removed as chronically unreliable (2026-07, in the pre-publication "rctest" repo this project started as) in favor of the browser extension, and this app now focuses solely on church-book citation tracking/display.

## Purpose
A Windows WPF desktop app (.NET 8, C# 12, MahApps.Metro, German UI) for genealogy research on online church-record scans — originally `data.matricula-online.eu`, now multiple providers behind `ICitationExtractor` (Matricula + DFG-viewer archives). The user connects the app to a running browser, clicks **Lesen** on a Matricula scan page, and gets the citation metadata (Land, Bistum, Pfarrei, Buchtyp, date range, Signatur, scan/page, links) in an always-on-top window — formatted as a **Quellenangabe** (the church book = the genealogy software's *source* record) and a **Zitatangabe** (the specific page = a *citation* on that source), both in user-definable formats and copyable per click. Finds can be annotated (Name/Kommentar) and saved persistently for later revisiting.

## Project Structure

```
Matrikelhelfer/
├── App.xaml/.cs                 # StartupUri -> Views/MainWindow.xaml
├── Models/
│   ├── MatriculaInfo.cs         # record: raw scraped citation fields (+ computed CitationTitle/BookLabel/BookUrl, [JsonIgnore])
│   ├── CitationStyle.cs         # record: named format template (Name + Template string)
│   └── SavedRecord.cs           # record: one persisted find (Id, SavedAt, Name, Comment, Info)
├── Browsers/                    # Per-browser address-bar lookup strategies
│   ├── IBrowserAddressBarLocator.cs
│   ├── AddressBarLocatorBase.cs # shared URL-shape fallback scan
│   ├── ChromiumAddressBarLocator.cs  # Chrome/Edge/Brave/Opera/Vivaldi/IE
│   └── FirefoxAddressBarLocator.cs
├── Services/
│   ├── BrowserConnection.cs     # manual browser bridge: list/pick browser, read URL, drive navigation
│   ├── ICitationExtractor.cs    # per-provider extraction strategy (CanHandle by URL)
│   ├── MatriculaExtractor.cs    # Matricula provider: HTTP fetch + HTML parse + cache
│   ├── DfgViewerExtractor.cs    # DFG-viewer (tx_dlf) provider: METS/MODS XML parse + cache
│   ├── NativeInput.cs           # SendInput-based keyboard simulation (Ctrl+L / Enter re-navigation)
│   ├── CitationTemplateEngine.cs # {Placeholder} template rendering over a MatriculaInfo
│   ├── CitationStyleCatalog.cs  # built-in templates: first-start seeds + fixed defaults
│   ├── FormatSettingsStore.cs   # persists format lists + active selections (formats.json, best-effort)
│   ├── SavedEntryStore.cs       # persists saved finds (entries.json, atomic writes, errors surfaced)
│   └── EntryCsvExporter.cs      # exports saved finds as German-Excel-friendly CSV
├── ViewModels/
│   ├── MainViewModel.cs         # owns connection+extractor, all main-window state
│   ├── FormatEditorViewModel.cs # backs the settings format editor (+ FormatItem, FormatTargetViewModel)
│   ├── RelayCommand.cs          # minimal ICommand (no MVVM toolkit dependency)
│   └── SavedEntry.cs            # wraps a SavedRecord for list display
└── Views/
    ├── MainWindow.xaml/.cs      # main UI + saved-entries Flyout (DataGrid)
    ├── SettingsWindow.xaml/.cs  # format editor dialog
    ├── BrowserPickerWindow.xaml/.cs  # "connect to which browser?" chooser
    ├── CopyableField.xaml/.cs   # reusable labeled field w/ copy button (opt-in wrap/edit/warn)
    └── CitationPreviewConverter.cs   # IMultiValueConverter: renders a style against DisplayedInfo
```

## Key Design Patterns

### MVVM
`MainWindow` binds to `MainViewModel` (constructed in code-behind); `SettingsWindow` to `FormatEditorViewModel`. Code-behind holds only pure view logic (caret insertion for placeholder chips, DataGrid double-click/right-click glue, initial sort description). Commands are hand-rolled `RelayCommand`s (CanExecute via `CommandManager.RequerySuggested`; background state changes need an explicit `CommandManager.InvalidateRequerySuggested()`).

**Accepted deviation**: `MainViewModel` opens dialogs directly (`BrowserPickerWindow`, `SettingsWindow`, `MessageBox`, `SaveFileDialog`) instead of going through an injected dialog-service abstraction. Deliberate for a single-window app with no automated UI tests — a service interface with exactly one implementation would be ceremony without benefit. Revisit only if ViewModel-level tests ever need to fake dialogs.

`CopyableField` is the reusable building block: optional leading icon + floating-watermark label + value + copy button (+ optional extra-action button, e.g. open URL / save image). Opt-in DPs: `IconKind` (a `PackIconFontAwesome6Kind` shown left of the label in a muted `Gray3`; default `None` collapses the icon column so icon-less fields lay out unchanged), `ValueWrapping` (long rendered text), `IsValueReadOnly=False` (editable fields also need `Mode=TwoWay` on their Value binding), `WarnWhenEmpty` (red border while empty — applied from code-behind as a **local** `BorderBrush` value because MahApps' own style/template triggers silently override a mere style-trigger setter).

### Manual browser connection (`Services/BrowserConnection`)
Replaced the earlier `BrowserWatcher` (WinEventHook + UIA property-changed events): the always-on hooks proved fragile, so the app is now **on-demand**. The plug button opens `BrowserPickerWindow` with the running browsers (`EnumWindows`, one visible top-level window per PID, filtered by the address-bar locators, with executable icons via System.Drawing); connecting just stores the target. **Lesen** then reads the address bar's UIA `ValuePattern` at that moment; no polling, no event subscriptions. If the browser process has exited, the connection is dropped and the UI reflects it.

**`TryNavigateAsync`** drives the connected browser to a URL (used by "Im Browser öffnen" / double-click on a saved entry): `SetForegroundWindow` + **poll** `GetForegroundWindow()` (activation is asynchronous — a fixed delay would be a race), then `NativeInput.SendChord(Ctrl, L)` to focus the omnibox the way a user would (`AutomationElement.SetFocus()` is unreliable on Chromium's custom-rendered omnibox), `ValuePattern.SetValue()` for the URL (works without real OS focus), and `SendKey(Enter)` to submit (needs the focus Ctrl+L got).

### Per-browser address-bar lookup (`Browsers/`)
`IBrowserAddressBarLocator.FindAddressBar(window)` encapsulates one browser family's UI Automation quirks. `AddressBarLocatorBase` implements the shared fallback (scan all controls of the declared `TypeCondition` for one whose value is URL-shaped) so concrete classes only declare `ProcessNames`, `TypeCondition`, and `PreciseCondition`:
- **`ChromiumAddressBarLocator`** (chrome/msedge/brave/opera/vivaldi/iexplore): omnibox is `ControlType.Edit`, matched by `Name` (English only; non-English UI falls through to the URL-shape fallback).
- **`FirefoxAddressBarLocator`** (firefox): urlbar is `ControlType.ComboBox` with a locale-dependent `Name` but a **stable `AutomationId` = `"urlbar-input"`** — matched by AutomationId first.

Adding a browser = one new class plus one registration line.

### Provider extractors (`Services/ICitationExtractor`)
Multiple church-book providers are supported through one strategy interface, mirroring the per-browser locator pattern: every provider yields the same `MatriculaInfo` shape and differs only in which site it understands and how the fields are scraped. `MainViewModel` holds the extractor list and routes by URL: `CanHandle(Uri)` (host check) picks the provider, then `GetInfoAsync(Uri)` scrapes; `DownloadImageAsync` is also per-provider (image fetches may need provider-specific headers) and is routed via the *displayed* record's stored page URL, so saved entries from any provider download correctly. Adding a provider = one new `ICitationExtractor` class plus one entry in `MainViewModel._extractors`.

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

### Source/citation format system
Genealogy software keeps *sources* (the church book) separate from *citations* on them (the page) — the app's two central fields mirror that split:
- `CitationTemplateEngine.Render(template, info)` does plain `{Token}` replacement. Placeholders (offered as chips in four topic rows — place / book / page / links+date): `Land, Bistum, Pfarrei, SignaturPfarrei` · `SignaturBuch, Signatur, Buchtyp, Von, Bis, JahrVon/Bis` · `Seite, Scan-ID, Scan-Nr` · `BookUrl, PageUrl, ImageUrl, AccessDate` (names match the main window's field labels). No legacy token aliases or migration paths — the app has no users besides its author; when tokens are renamed, locally saved `formats.json` templates are simply re-edited (or the file deleted to re-seed).

**Per-format date style**: each `CitationStyle` carries a `DateFormat` id (persisted in formats.json; missing on old files → "original" via the record's constructor default). The editor's Datumsformat dropdown picks from `CitationTemplateEngine.DateStyles` (original / 22.03.1845 / 22. März 1845 / 22 MÄR 1845 GEDCOM / ISO / three English variants) and it applies to `{Von}`, `{Bis}` and `{AccessDate}` on render. Provider date strings are parsed with the known spellings (Matricula "1. Januar 1670", AEM "01.01.1846"); an unparseable date passes through **verbatim** — a citation must never invent a date. Gotcha: .NET's German `MMM` keeps "Juni"/"Juli"/"März" unabbreviated, so the GEDCOM style uses a fixed 3-letter month table. `{BookUrl}` is the URL without `?pg=` (identifies the book). `{Seite}` renders **empty** when no page is confirmed — deliberately no scan-number fallback (a citation must not pass a scan index off as a page).
- Users maintain **two format lists** (Quellen-/Zitatformate) in the settings dialog: a target dropdown switches the visible list; each list's selection is its *active* format. Editor pane = name + template + clickable placeholder chips (bare names, braces added on insertion at the caret; chips are `Focusable=False` so the TextBox keeps its caret) + live preview against a fixed `SampleInfo`. OK/Cancel dialog semantics (the VM edits a copy).
- The main window shows both fields as **ComboBoxes with rendered previews**: one shared `FormatPreviewTemplate` (format name + `CitationPreviewConverter` output against `DisplayedInfo`) serves the closed box (WPF's SelectionBoxItemTemplate behavior) and every popup entry. Selecting a format re-renders and persists immediately.
- `FormatSettingsStore` persists both lists + active selections to `%APPDATA%\Matrikelhelfer\formats.json` (best-effort: failures never take the app down). First-start seed: `CitationStyleCatalog.SourceSeed`/`CitationSeed` (user-curated: Sehr kurz / Kurz / Ausführlich / Chicago sources; Standard / Nur Seite / Kurz citations) with "Kurz" starting active in both lists (`Default*Name`); each list seeds independently if emptied.

### No page guessing (editable Seite)
The number in Matricula's scan label rarely matches the real handwritten page number, so the parsed value is discarded on every read: the Seite field starts **empty** (red-border warning via `WarnWhenEmpty`) and only ever holds what the user typed. An edit is pushed back into the displayed/current record (`info with { Page = ... }`) so `{Seite}` renders and saves with it; clearing it means "page unknown".

### Saved finds (persistent)
- `SavedRecord` (Models) is the storage shape: `Id` (GUID), `SavedAt`, `Name`, `Comment`, `Info` (the full `MatriculaInfo`). Storage is deliberately **flat** — hierarchy (Land → Bistum → Pfarrei → Buch, a future tree view) is derived at display time, never stored. Rendered citation text is never stored either: display always re-renders with the *current* formats.
- `SavedEntryStore` persists to `%APPDATA%\Matrikelhelfer\entries.json` with a `version` field. **Decision log: JSON over SQLite** — entries accrue by manual clicks (thousands at most), and a readable, backup-friendly file beats a binary DB for personal research data; if search/tags/10k+ entries materialize, swap this class's internals to SQLite plus a one-time import. Writes are **atomic** (temp file + `File.Replace`) and failures are **surfaced** in the status line (unlike the best-effort format store — these entries can represent years of research).
- Main-window flow: `Name` and `Kommentar` (together in the "Notizen" section at the top) annotate the current page. **Lesen always starts fresh**: every read attempt first clears ALL fields (a failed read must never leave the previous record standing next to its error message — it reads as success). If Name/Kommentar/Seite hold input not covered by a saved entry, a discard-confirmation dialog guards both **Lesen** and **selecting a saved entry** (the selection guard fires at most once per browsing session: once an entry is displayed, record+notes match a saved entry and the guard stays silent; rejecting the selection snaps the grid highlight back via a `Dispatcher.BeginInvoke`d re-notification). Selecting an entry also syncs `_currentInfo` to the displayed record — Speichern and the duplicate check act on it, so an edited Kommentar saves against the *displayed* entry, not the last-read page. All actions are circle icon buttons in the top row (connect + Lesen left; Speichern + list toggle + settings right); **Lesen** is the primary action and gets a filled-accent circle to stand out. The fields column scrolls (`ScrollViewer`) and its sections (Notizen / Quelle / Zitat / Links) are grouped into subtle rounded Gray9 "cards" (`SectionCardStyle`); each field carries a leading `CopyableField` icon (the Quelle/Zitat dropdowns have none but are left-indented by the icon-column width to stay flush with the fields above). Status/error messages live in a one-line status bar at the window bottom (ellipsis-trimmed, full text in its tooltip — keep new status texts short). **Speichern** persists immediately and is disabled while record+notes exactly match an existing entry (accidental repeat click) — same page with different notes stays saveable (several finds per page are normal).
- The list UI is a **docked right-hand panel** (not a Flyout — a Flyout overlays and hides the fields): a top bar with the CSV-export button (a circle button sized/aligned to the main window's top-row buttons across the splitter) over a Gray9 "card" `Border` that holds the "Gespeicherte Einträge" heading and a sortable read-only DataGrid (Name | Buch | Seite | Gespeichert; default newest-first; comment as row tooltip). Single click redisplays the entry in the main fields; **double-click** (or context menu) drives the browser back to the scan; Entf/context menu deletes (persisted). The card **hugs its content** (`VerticalAlignment=Top` + a `DockPanel`-filled DataGrid) rather than filling the column, and its `MaxHeight` is bound to an invisible sizer `Border` that stretches the star row — so the cap tracks the window with no pixel constant, and once the list outgrows it the DataGrid scrolls internally (binding `MaxHeight` to the whole panel's height instead overshoots by the top bar and pushes the scrollbar off-screen).
- **CSV export** (button in the panel header → `EntryCsvExporter`): all raw fields (incl. the `SignaturPfarrei`/`SignaturBuch` split) plus the Quellen-/Zitatangabe *rendered with the currently active formats* — the stored data stays raw, but an export is a snapshot for outside the app (the user's own long-term archive, e.g. "did I work through this book years ago?"). Format targets German Excel: `;` separator and a UTF-8 BOM (without the BOM, Excel guesses ANSI and mangles umlauts on double-click); standard CSV quoting handles multi-line comments.
- Panel geometry (`MainWindow.xaml.cs`, `UpdateEntriesPanel`): while OPEN the column roles invert — the fields column is frozen at its pixel width and the panel column is star-sized, so the toggle's window-width animation *and* manual window resizing only ever change the panel, never the fields. A GridSplitter adjusts the split (its chosen width is remembered across toggles); the splitter column's width must be part of the window-width delta on both open and close, or each cycle leaks it into the window width. The splitter **stretches across the whole gap** between the two panels (the fields grid drops its right margin while OPEN so the splitter column owns the gap, making the entire space the drag handle instead of an off-centre sliver); on close `UpdateEntriesPanel` restores the fields grid's symmetric margin. The fields `ScrollViewer` adds right padding **only while its scrollbar is showing** (a trigger on `ComputedVerticalScrollBarVisibility`) — a constant padding would inset the cards but not the button row above, making the closed-window right gap wider than the left.

## Data Flow

1. Plug button → `BrowserPickerWindow` → `BrowserConnection.Connect` (stores the chosen browser window).
2. **Lesen** → `BrowserConnection.ReadActiveTabUrl()` (UIA ValuePattern) → URL routed to the matching `ICitationExtractor` (`CanHandle`) → `GetInfoAsync(uri)` → `DisplayInfo` populates all fields and renders Quellen-/Zitatangabe with the active formats; Name/Kommentar/Seite reset.
3. **Speichern** → `SavedRecord` → `SavedEntries` (ObservableCollection, bound to the flyout grid) → `SavedEntryStore.Save` (atomic).
4. Selecting a saved entry → `DisplayInfo(entry.Info)` + restore Name/Kommentar; double-click additionally → `TryNavigateAsync(entry.Info.Url)`.
5. Settings dialog OK → new `FormatSettings` → both dropdowns refresh, fields re-render, `FormatSettingsStore.Save`.

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
