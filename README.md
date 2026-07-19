# Matrikelhelfer

A Windows desktop app for genealogists working with digitized church books ("Matrikel"). While you view a church-book scan in your browser, Matrikelhelfer pulls out all the data a proper source citation needs — diocese, parish, book type and date range, archive signature, scan number, links — and turns it into ready-to-paste **source and citation lines** in user-definable formats, matching how genealogy software organizes records (one *source* per church book, one *citation* per finding).

Findings can be annotated (name, comment) and saved permanently, so you can return to them later — or export everything as a CSV for your own long-term archive.

**Supported providers** (auto-detected from the browser URL):

- [Matricula Online](https://data.matricula-online.eu) — church books from Germany, Austria, Luxembourg and more
- **DFG-Viewer**-based archives (`tx_dlf`), currently the [Digital Archive of the Archdiocese of Munich and Freising](https://digitales-archiv.erzbistum-muenchen.de)

The UI is in German, as is the [user manual](https://matrikelhelfer.niggl-schlagbauer.de/).

**Download:** grab the installer or portable ZIP from the [latest release](https://github.com/luni64/Matrikelhelfer/releases/latest) (Windows 10/11, [.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0)).

## How it works

Matrikelhelfer connects to a running browser (Chrome, Edge, Brave, Opera, Vivaldi, or Firefox) on demand: clicking **Lesen** reads the address bar once via Windows UI Automation — there is no background watching. The church-book data itself is fetched directly from the provider (server-rendered HTML for Matricula, METS/MODS XML for DFG-Viewer archives) and cached per book, so paging through scans causes no repeat traffic. Nothing is sent anywhere; all saved data lives locally under `%APPDATA%`.

See [`docs/ARCHITECTURE.md`](docs/ARCHITECTURE.md) for the full picture — the provider-extractor pattern, the per-browser address-bar locators, and the non-obvious gotchas hit along the way.

## Requirements

- Windows 10/11
- [.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0) (SDK to build)
- One of the supported browsers

## Build & run

```
dotnet build Matrikelhelfer.slnx
dotnet run --project Matrikelhelfer/Matrikelhelfer.csproj
```

If a previous run is still open, close it first — Windows keeps the executable locked and the build will fail (`MSB3027`).

## Documentation

- [User manual (German)](docs/Manual/index.md) — preview locally with `mkdocs serve`
- [Architecture](docs/ARCHITECTURE.md) — WPF app
- [Release process](docs/release/RELEASE_PROCESS.md)

## License

MIT — see [LICENSE.txt](LICENSE.txt). Third-party dependency licenses are listed in [THIRD_PARTY_LICENCES.md](THIRD_PARTY_LICENCES.md).
