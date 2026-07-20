# Next Release

## Features

- Third provider: **ARCHION** (archion.de), the German Protestant church-book portal. Its citation metadata is login-gated (a server fetch without the browser's session returns a stripped page), so the extractor reads the **logged-in browser tab title** — which carries the full breadcrumb (Land / Archiv / … / Pfarrei / Buch with date range) — instead of fetching. Detected automatically from the URL. No image URL, archival signature, or sequential scan number (ARCHION exposes none); Seite stays manual. **Experimental:** when the viewer's Permalink panel is open, the exact-page permalink (`archion.de/p/…`) is read from the rendered page (UI Automation) and used as `{PageUrl}` — otherwise the page link falls back to the address-bar (book) URL. Model gained `PageUrl`/`EffectivePageUrl`; extractors now receive a `PageContext` (URL + tab title + on-demand page-link lookup).

- BibTeX in the first-start format seed: a **BibTeX** source format (a `@misc` bibliography entry) and a matching **BibTeX (\cite)** citation format. The cite key leads with the parish (human-recognizable in a `.tex` source) plus the signature.
- Template modifier `{Token:clean}`: turns a value into a key-safe identifier — transliterates umlauts (ä→ae), strips other accents, replaces runs of spaces/commas/periods with `_` (keeping hyphens like `3-01`), and renders an empty field as `EMPTY` so it stands out in the output. General-purpose (works on any placeholder); introduced for BibTeX cite keys.

## Bug Fixes
