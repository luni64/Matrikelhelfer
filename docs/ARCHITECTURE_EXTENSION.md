# Matrikelhelfer — Architecture Overview (browser extension)

> This document covers the browser extension (`extension/`) only. For the WPF desktop app (`Matrikelhelfer/`), see `docs/ARCHITECTURE.md`. Both display a Matricula citation, but Ancestry citation-filling now lives **only** here: the WPF app had a UI-Automation-based equivalent (`SiteFillers/AncestrySiteFiller`) that was removed as chronically unreliable (coordinate-based clicking into a third-party page never worked consistently) in favor of this DOM-level approach, which doesn't have that class of problem.

## Purpose
A cross-browser (Firefox + Chromium) MV3 extension that watches `data.matricula-online.eu` scan pages, extracts the same citation metadata as the WPF app, shows it in a popup/sidebar for copy/paste, and can fill Ancestry's "Quellenangabe hinzufügen" (Add Citation) dialog directly — reading and writing page DOM in-process rather than reaching in from outside via accessibility APIs.

## Project Structure
```
extension/
├── manifest.json    MV3, cross-browser: Firefox toolbar popup + sidebar, Chromium toolbar popup
├── common.js        shared MH global: scoring/matching, year/format helpers, storage.local API shim
├── matricula.js     content script (data.matricula-online.eu): extracts the citation from the live
│                    DOM, publishes storage.local.currentCitation, polls the URL for ?pg= changes
├── ancestry.js       content script (ancestry.*): injects a "Matricula-Zitat einfügen" button into
│                    the Add-Citation dialog, fills fields, auto-selects or offers a candidate list
└── popup.html/css/js  popup AND Firefox sidebar page: current citation, Merken/Kopieren,
                     persistent saved list (storage.local)
```
No build step and no `node_modules` — plain scripts loaded via `<script>`/manifest order, since MV3 content scripts can't use ES module imports. All three of `common.js`, `matricula.js`/`ancestry.js`/`popup.js` hang everything off the single global `MH` object.

## Key Design Patterns

### `storage.local` as the shared state bus
There's no background service worker — `matricula.js` (running on the Matricula tab) and `ancestry.js` (running on the Ancestry tab) never talk to each other directly. Instead `matricula.js` writes `currentCitation` to `browser.storage.local`/`chrome.storage.local`, and the popup listens for changes via `storage.onChanged`. `ancestry.js` reads `currentCitation` on demand (when its injected button is clicked) rather than subscribing live, since it only needs the value at fill time. `savedCitations` and `sourceMappings` (the "remember this book's chosen source" map, keyed by `bookKey`) are the other two keys persisted the same way — this makes the saved list and remembered mappings **survive a browser restart**, unlike the WPF app's in-memory-only `SavedEntries`.

### `MH` (`common.js`) — logic shared across content scripts and the popup
Rather than duplicating scoring/formatting logic per script, `MH` centralizes it:
- `yearOf`, `citationTitle`, `pageDescription`, `displayText`, `format` — direct ports of `MatriculaInfo`'s computed properties and `MainViewModel.Format` from the WPF app, kept in sync by convention (each has a comment noting what it ports).
- `evaluate(optionText, citation)` — the fuzzy-matching engine for scoring an Ancestry source-list entry against a citation. This has diverged from (and gone further than) the WPF `AncestrySiteFiller.Score()` it started as a port of:
  - `withinOneEdit` gives typo tolerance (one insert/delete/substitute) for longer words, since Ancestry's source titles are free-text and crowd-sourced ("Waliting" ~ "Walting").
  - `parishMatch` strips Matricula's diocese suffix (`"Walting-EI"` → `"Walting"`) and gates on a *distinctive* (longest) name token appearing in the option, handling compound big-city parish names (`"München, Haidhausen, St. Johann"`) by requiring a church/district component, not just the city, so sibling parishes in the same city don't false-positive.
  - `typeSynonyms` maps each Matricula `Buchtyp` to a word list *and* the genealogy shorthand symbols used in crowd-sourced titles (`*` = birth/baptism, `oo` = marriage, `+` = death).
  - `signaturMatches` requires the Signatur to appear as a delimited token (not a substring of a longer number — a bare `"9"` shouldn't match inside `"1859"`).
  - The result carries a `certain` flag (parish + Signatur both matched) separate from the numeric score — this is the actual auto-select gate in `ancestry.js` (see below), not a score threshold.

### Extraction reads the live DOM directly (no HTTP fetch, no cache)
Unlike the WPF app's `MatriculaExtractor` (which re-fetches and caches HTML separately from whatever the browser is showing), `matricula.js` reads the *actual rendered page* the content script is injected into — there's no second network round-trip and no path-keyed cache to invalidate, since the DOM already reflects wherever the user has navigated. The `labels` array (priest-written page labels, index-aligned with `?pg=`) is extracted with the same "only the array is strict JSON, not the surrounding object literal" regex approach as the WPF extractor, for the same reason (a bare-identifier JS value elsewhere in the object breaks `JSON.parse` on the whole thing).

**Polling, not a load event**: Matricula's page-turn control changes `?pg=` without a full page navigation, so `matricula.js` polls `location.href` every 500ms and re-extracts on change — the DOM equivalent of the WPF app's UIA `AutomationPropertyChangedEventHandler` on the address bar, just without an event to hook.

### Filling Ancestry: DOM assignment instead of coordinate clicking
This is the core simplification over the WPF approach: Ancestry's hidden `<select id="addCitationSourceSelectorId">` is a real DOM element reachable by `document.getElementById`, so `ancestry.js` selects a source with `select.value = option.value` + a dispatched `change` event — no coordinate-based click, no virtualized-list scrolling, no "hidden mirror vs. rendered row" disambiguation (all UIA-side problems that don't exist at the DOM level). One quirk carries over: the visible "Titel der Quelle" display (`input.calloutTrigger`) doesn't necessarily update its own text on a programmatic `select` change, so `applySelection` mirrors the chosen option's text into it manually.

**React's shadow value** — Ancestry's form fields are React-controlled, so a plain `el.value = text` gets silently reverted on the next render (React tracks its own value separately from the DOM property). `setInputValue` works around this by calling the *native* `HTMLInputElement`/`HTMLTextAreaElement` value setter via `Object.getOwnPropertyDescriptor(proto, "value").set.call(el, text)` (bypassing any subclass/React override of the setter) and then dispatching synthetic `input`/`change` events so React's `onChange` handlers still fire.

**Confident auto-select vs. showing a candidate list**: `fill()` only auto-selects when the top match is `certain` (parish + Signatur) *and* clearly beats the runner-up by `CLEAR_WINNER_MARGIN` (20) — parish+type alone isn't enough, since a parish can have several same-type volumes that only differ by an unreliable crowd-sourced year. Otherwise it renders an in-page candidate list (`showCandidateList`, the DOM equivalent of the WPF `SourcePickerWindow`) with clickable rows; picking one also offers "remember this choice" the same way. A remembered `sourceMappings` entry (keyed by `pfarrei|buchtyp|signatur`, normalized/lowercased) short-circuits both the scoring and the candidate list on repeat visits, same idea as the WPF `SourceMappingStore` but with an inline "forget and re-search" undo link instead of a separate flow.

**Dialog re-creation**: the Add-Citation dialog is created on demand and can be torn down/recreated (discarding a previously injected button), so `injectButton` is driven by a `MutationObserver` on `document.body` rather than a one-shot injection at content-script load.

## Data Flow
1. User navigates to a Matricula scan page → `matricula.js` extracts the citation from the DOM and writes it to `storage.local.currentCitation`.
2. Popup/sidebar (if open) re-renders live via `storage.onChanged`; `Merken` appends the current citation to `storage.local.savedCitations`.
3. User navigates to Ancestry and opens "Add Citation" → `ancestry.js`'s `MutationObserver` detects the dialog and injects the "Matricula-Zitat einfügen" button.
4. Clicking the button reads `currentCitation` back out of `storage.local`, fills the detail/URL text fields, and either auto-selects a source, applies a remembered mapping, or shows the in-page candidate list for the user to click.

## Known Gaps / TODO (see `extension/README.md` for the up-to-date list)
- **Not yet verified live**: the "Neue Quelle erstellen" (new-source) tab flow and the remembered-mapping round trip — field lookups and label texts for these come from the UIA-era WPF findings and need one live pass against the real Ancestry DOM.
- English-UI Ancestry and Matricula label texts aren't handled (German UI only, same limitation as the WPF app).
- No icons yet (browsers show a default puzzle piece); needed before store submission.
- Firefox requires signing for permanent installs (free via addons.mozilla.org, or `web-ext sign` for unlisted self-distribution); Chrome Web Store requires the one-time $5 developer fee. Temporary installs (`about:debugging`/"Load unpacked") are unsigned and vanish on browser restart.

## External Dependencies
None — vanilla JS content scripts + the standard WebExtension `storage`/`clipboardWrite` APIs. No bundler, no npm packages.

## Conventions
- Plain scripts (no ES modules, no TypeScript) — content scripts can't `import`, so everything shares state through the `MH` global and manifest/script-tag load order.
- Logic ported from the WPF app is commented with what it's a port of, so the two implementations can be told apart from a drift-detection standpoint (e.g. "port of `MatriculaInfo.ExtractYear`") — check both when changing shared behavior like scoring or field extraction.
- German-only UI strings/labels throughout, matching the WPF app and the target sites' primary language for this user.
