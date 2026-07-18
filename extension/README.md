# Matrikelhelfer — browser extension

Browser-extension reimplementation of the WPF app: watches Matricula Online
scan pages, shows/collects citation metadata, and fills Ancestry's
"Quellenangabe hinzufügen" dialog — all at DOM level. The WPF app had a
UI-Automation-based equivalent of the Ancestry-fill feature; it was removed
as chronically unreliable, so this extension is now the only place that
feature lives.

## Structure

```
extension/
├── manifest.json    MV3, cross-browser (Firefox sidebar + toolbar popup)
├── common.js        shared MH global: scoring, year/format helpers, API shim
├── matricula.js     content script: extracts citation from the live DOM,
│                    publishes storage.local.currentCitation (polls the URL
│                    because ?pg= changes without a page load)
├── ancestry.js      content script: injects "Matricula-Zitat einfügen" into
│                    the Add-Citation dialog, fills fields, preselects the
│                    best-scored source in #addCitationSourceSelectorId
└── popup.html/css/js  popup AND Firefox sidebar page: current citation,
                     Merken/Kopieren, persistent saved list (storage.local)
```

## Try it (Firefox)

1. `about:debugging` → **This Firefox** → **Load Temporary Add-on…** →
   select `extension/manifest.json`.
2. Open a Matricula scan page — the popup (toolbar icon) or sidebar
   (View → Sidebar → Matrikelhelfer) should show the citation and update
   when paging through the book.
3. On Ancestry, open a person → Quelle hinzufügen: the dialog should contain
   the injected button. Diagnostics go to the page's devtools console,
   prefixed `[Matrikelhelfer]`.

Temporary add-ons vanish when Firefox closes — reload after each restart
(or use `web-ext run`). Chromium: `chrome://extensions` → Developer mode →
"Load unpacked" → the `extension/` folder (warns about the Firefox-only
manifest keys; harmless).

## Known gaps / TODO before distribution

- **Verified live (2026-07-13):** citation extraction incl. page-turning,
  field filling, source auto-select via the hidden select (a real Ancestry
  save confirmed the submitted value), and the injected candidate list.
  **Not yet verified live:** the "Neue Quelle erstellen" tab flow
  (tab-element lookup + title field) and the remembered-mapping round trip.
- English-UI Ancestry label texts are not handled yet (German UI only).
- **Matricula field labels** assume the site's German labels ("Buchtyp",
  "Datum von", …), same as the WPF extractor.
- **No icons yet** (browsers show a default puzzle piece); need 48/96 px
  PNGs before store submission.
- **Signing/distribution:** Firefox requires signing for permanent installs —
  free via addons.mozilla.org (listed, or unlisted self-distribution with
  `web-ext sign`). Chrome Web Store requires the one-time $5 developer fee.
- The saved list has no export yet (the WPF app didn't either).
