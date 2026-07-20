# Next Release

## Features

- Third provider: **ARCHION** (archion.de), the German Protestant church-book portal. Its citation metadata is login-gated (a server fetch without the browser's session returns a stripped page), so the extractor reads the **logged-in browser tab title** — which carries the full breadcrumb (Land / Archiv / … / Pfarrei / Buch with date range) — instead of fetching. Detected automatically from the URL. No image URL, archival signature, or sequential scan number (ARCHION exposes none); Seite stays manual. **Experimental:** when the viewer's Permalink panel is open, the exact-page permalink (`archion.de/p/…`) is read from the rendered page (UI Automation) and used as `{PageUrl}` — otherwise the page link falls back to the address-bar (book) URL. Model gained `PageUrl`/`EffectivePageUrl`; extractors now receive a `PageContext` (URL + tab title + on-demand page-link lookup).

- BibTeX in the first-start format seed: a **BibTeX** source format (a `@misc` bibliography entry) and a matching **BibTeX (\cite)** citation format. The cite key leads with the parish (human-recognizable in a `.tex` source) plus the signature.
- Template modifier `{Token:clean}`: turns a value into a key-safe identifier — transliterates umlauts (ä→ae), strips other accents, replaces runs of spaces/commas/periods with `_` (keeping hyphens like `3-01`), and renders an empty field as `EMPTY` so it stands out in the output. General-purpose (works on any placeholder); introduced for BibTeX cite keys.
- BibTeX-library export: an "@" button in the saved-entries panel writes a `.bib` file — one `@misc` entry per unique **book** (finds are deduplicated down to their books), rendered with your "BibTeX" source format, with cite keys made unique (`a`/`b`/`c` suffixes) so the file always compiles. If no "BibTeX" format exists, it offers to add the built-in one.

## Bug Fixes
- Matricula pages read on the English site (`/en/…`) left Buchtyp, Signatur and the dates empty — those fields are found by their German labels, which are English there (and the date *values* are localized too). The extractor now always reads the `/de/` version regardless of the browsing language (links still point to the page you opened).
