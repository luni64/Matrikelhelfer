# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project

Matrikelhelfer (formerly "rctest"/"Matricula Helper") is a Windows WPF desktop app (.NET 8, C# 12, German UI) for genealogists: connected on demand to a running browser, it reads the church-book scan page currently shown (Matricula Online or DFG-Viewer archives), extracts the citation metadata (diocese, parish, book, date range, signature, scan number, links) and renders copyable Quellen-/Zitatangaben in user-definable formats. Finds can be annotated and saved persistently, and exported as CSV. Single project, no test suite exists in this repo.

A browser-extension reimplementation of the citation-display idea lives in `extension/` (DOM-based instead of UI-Automation-based, includes Ancestry citation filling). The WPF app (`Matrikelhelfer/`) is the active development focus.

**Read `docs/ARCHITECTURE.md` first** — it is the authoritative architecture reference for the WPF app (project structure, the on-demand browser connection, the per-browser locator pattern, the per-provider extractor pattern with its fetch/caching strategy, and a list of non-obvious gotchas hit during development). `docs/ARCHITECTURE_EXTENSION.md` is the equivalent for `extension/`. Keep the relevant one in sync when architecture changes.

## Commands

```
dotnet build Matrikelhelfer.slnx
dotnet run --project Matrikelhelfer\Matrikelhelfer.csproj
dotnet publish Matrikelhelfer\Matrikelhelfer.csproj -c Release   # produces the win-x64 build used by the installer
```

There is no automated test project — verification is manual (run the app, connect it to a browser showing a matricula-online.eu book page, click Lesen, confirm the fields populate).

**Before rebuilding**, check for and close a leftover running instance — otherwise the build fails with a file-lock error (`MSB3027`), since Windows keeps `Matrikelhelfer.exe` locked while it's running (e.g. left open from a manual test):
```
tasklist /FI "IMAGENAME eq Matrikelhelfer.exe"
taskkill /F /IM Matrikelhelfer.exe
```

## Architecture summary

**Read `docs/ARCHITECTURE.md` for the full picture of the WPF app** — the source of truth for MVVM structure, the on-demand browser connection, per-browser locators, and the per-provider extractor/caching strategy; update it when WPF architecture changes. **`docs/ARCHITECTURE_EXTENSION.md`** is the equivalent for the browser extension in `extension/`; update it when extension architecture changes.

## Coding conventions

- Nullable reference types enabled (`<Nullable>enable</Nullable>`) — respect annotations, use `is null` / `is not null`.
- MVVM: no business logic in code-behind; ViewModel properties expose private setters via a `SetField` helper.
- Prefer async/await for I/O (HTTP fetch); never block the UI thread.

## Working conventions

- Never commit, stage, or push without an explicit user request — this includes intermediate steps of a larger task; make the changes and let the user review/test before committing anything.
- **When a commit is requested**, first update the relevant architecture doc (`docs/ARCHITECTURE.md` for `Matrikelhelfer/`, `docs/ARCHITECTURE_EXTENSION.md` for `extension/`) if the change touched architecture, and add an entry to `docs/release/NEXT_RELEASE.md` (Features/Bug Fixes) describing the change, then include those doc updates in the same commit.
- Prefer direct file-edit tools over terminal-based file writing.
- This is a personal tool with a small audience — keep fixes targeted; don't add resilience for hypothetical provider site redesigns or unverified browsers unless asked. No users besides the author yet, so no upgrade/migration code for renamed settings or formats.
- Repo: `luni64/Matrikelhelfer` on GitHub (`gh` CLI already authenticated as `luni64`).

## Release process

- `docs/release/NEXT_RELEASE.md` is the scratchpad for the unreleased version — add feature/bug-fix notes here as you go.
- **`docs/release/RELEASE_PROCESS.md` is the authoritative step-by-step release guide** (version bump, doc rolling, win-x64 publish, ZIP, signed installer via ISCC, draft GitHub release, publish after testing) — modeled on the AutoNum project's process. Follow it when asked to prepare a release.
- `installer/` holds the Inno Setup script (`setup.iss`), the vendored `CodeDependencies.iss` (InnoDependencyInstaller), and `WHATS_NEW.template`. Signing reuses the `certum` Inno Setup Sign Tool profile already configured on this machine (shared with AutoNum, not per-project).
- No app icon exists yet — see the "Not yet set up" note in `installer/README.md`. License: MIT (`LICENSE.txt`), dependency licenses listed in `THIRD_PARTY_LICENCES.md`.
- `mkdocs.yml` + `docs/Manual/` build the German user manual site (`mkdocs serve`/`mkdocs build` to preview locally), modeled on AutoNum's. Deployed to GitHub Pages by `.github/workflows/manual.yml`, which is **on-demand only** (`workflow_dispatch`) — pushing manual changes keeps the source current in the repo but does **not** publish; deploy deliberately at release time via the Actions tab or `gh workflow run manual.yml`. Served at `https://matrikelhelfer.niggl-schlagbauer.de/` (custom domain configured in the repo's Pages settings, not via a repo `CNAME` file — same as AutoNum).
