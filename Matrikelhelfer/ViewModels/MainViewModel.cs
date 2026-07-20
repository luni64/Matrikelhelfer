using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Input;
using Matrikelhelfer.Models;
using Matrikelhelfer.Services;
using Matrikelhelfer.Views;

namespace Matrikelhelfer.ViewModels;

class MainViewModel : INotifyPropertyChanged
{
    readonly BrowserConnection _connection = new();

    // All known church-book providers; the URL read from the browser picks
    // the matching one via CanHandle. Adding a provider = one new
    // ICitationExtractor class plus one entry here (same pattern as the
    // per-browser address-bar locators).
    readonly ICitationExtractor[] _extractors =
        [new MatriculaExtractor(), new DfgViewerExtractor(), new ArchionExtractor()];

    MatriculaInfo? _currentInfo;

    // The user's source- and citation-format templates, edited via the
    // settings dialog and persisted across restarts. Shown in the two
    // format dropdowns; the selected one of each list renders the
    // Quellen-/Zitatangabe.

    // Status/error line, shown in the one-line status bar at the bottom of
    // the window - keep new texts short (long ones get ellipsis-trimmed).
    string _statusText = "Nicht verbunden – Browser über das Steckersymbol wählen.";
    public string StatusText
    {
        get => _statusText;
        private set => SetField(ref _statusText, value);
    }

    string _bistumText = "";
    public string BistumText
    {
        get => _bistumText;
        private set => SetField(ref _bistumText, value);
    }

    string _parishText = "";
    public string ParishText
    {
        get => _parishText;
        private set => SetField(ref _parishText, value);
    }

    string _bookText = "";
    public string BookText
    {
        get => _bookText;
        private set => SetField(ref _bookText, value);
    }

    string _signatureText = "";
    public string SignatureText
    {
        get => _signatureText;
        private set => SetField(ref _signatureText, value);
    }

    // Editable: the number parsed from the scan label rarely matches the
    // real handwritten page number, so the user can correct it by hand. The
    // override is pushed back into the displayed/current record so {Seite}
    // renders and saves with it.
    string _pageText = "";
    public string PageText
    {
        get => _pageText;
        set
        {
            if (_pageText == value)
            {
                return;
            }
            _pageText = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(PageText)));
            ApplyPageOverride(value);
        }
    }

    // User-entered notes for the entry about to be saved: whose record was
    // found on this page and why it is interesting. Cleared on every read.
    string _nameText = "";
    public string NameText
    {
        get => _nameText;
        set { SetField(ref _nameText, value); InvalidateDuplicateCheck(); }
    }

    string _commentText = "";
    public string CommentText
    {
        get => _commentText;
        set { SetField(ref _commentText, value); InvalidateDuplicateCheck(); }
    }

    string _pageIdText = "";
    public string PageIdText
    {
        get => _pageIdText;
        private set => SetField(ref _pageIdText, value);
    }

    string _urlText = "";
    public string UrlText
    {
        get => _urlText;
        private set => SetField(ref _urlText, value);
    }

    string _imageUrlText = "";
    public string ImageUrlText
    {
        get => _imageUrlText;
        private set => SetField(ref _imageUrlText, value);
    }

    // The fixed-format source/citation split for genealogy software: the
    // "Quellenangabe" identifies the book (source record), the "Zitatangabe"
    // the specific page/scan (citation on that source).
    string _sourceText = "";
    public string SourceText
    {
        get => _sourceText;
        private set => SetField(ref _sourceText, value);
    }

    string _pageCitationText = "";
    public string PageCitationText
    {
        get => _pageCitationText;
        private set => SetField(ref _pageCitationText, value);
    }

    // The MatriculaInfo currently on screen (last read OR redisplayed saved
    // entry - _currentInfo follows it in both cases). Public/bindable so
    // the format ComboBox's per-item template can render a live preview of
    // what each style would look like, not just the currently selected one.
    MatriculaInfo? _displayedInfo;
    public MatriculaInfo? DisplayedInfo
    {
        get => _displayedInfo;
        private set => SetField(ref _displayedInfo, value);
    }

    // The format lists are wholesale-replaced after the settings dialog,
    // hence plain notify properties instead of mutated ObservableCollections.
    IReadOnlyList<CitationStyle> _sourceFormats;
    public IReadOnlyList<CitationStyle> SourceFormats
    {
        get => _sourceFormats;
        private set => SetField(ref _sourceFormats, value);
    }

    IReadOnlyList<CitationStyle> _citationFormats;
    public IReadOnlyList<CitationStyle> CitationFormats
    {
        get => _citationFormats;
        private set => SetField(ref _citationFormats, value);
    }

    CitationStyle _selectedSourceFormat;
    public CitationStyle? SelectedSourceFormat
    {
        get => _selectedSourceFormat;
        set
        {
            // Ignore the transient null WPF pushes through the two-way
            // SelectedItem binding while ItemsSource is being swapped after
            // the settings dialog.
            if (value is null || Equals(_selectedSourceFormat, value))
            {
                return;
            }
            _selectedSourceFormat = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SelectedSourceFormat)));
            if (DisplayedInfo != null)
            {
                SourceText = CitationTemplateEngine.Render(value, DisplayedInfo);
            }
            SaveFormats();
        }
    }

    CitationStyle _selectedCitationFormat;
    public CitationStyle? SelectedCitationFormat
    {
        get => _selectedCitationFormat;
        set
        {
            if (value is null || Equals(_selectedCitationFormat, value))
            {
                return;
            }
            _selectedCitationFormat = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SelectedCitationFormat)));
            if (DisplayedInfo != null)
            {
                PageCitationText = CitationTemplateEngine.Render(value, DisplayedInfo);
            }
            SaveFormats();
        }
    }

    string _connectionStatusText = "Nicht verbunden";
    public string ConnectionStatusText
    {
        get => _connectionStatusText;
        private set => SetField(ref _connectionStatusText, value);
    }

    bool _isConnected;
    public bool IsConnected
    {
        get => _isConnected;
        private set => SetField(ref _isConnected, value);
    }

    public ObservableCollection<SavedEntry> SavedEntries { get; } = new();

    // Selecting an entry only redisplays it in the main fields - driving
    // the browser back to the scan is reserved for an explicit double-click
    // (NavigateToSelectedEntry), so skimming the list doesn't yank the
    // browser around.
    SavedEntry? _selectedSavedEntry;
    public SavedEntry? SelectedSavedEntry
    {
        get => _selectedSavedEntry;
        set
        {
            // Same guard as Lesen: selecting an entry overwrites the
            // fields, so unsaved input must be confirmed away first. Fires
            // at most once per browsing session - once an entry is shown,
            // record+notes match a saved entry and the guard stays silent.
            if (value is not null && !Equals(value, _selectedSavedEntry) && !ConfirmDiscardUnsaved())
            {
                // The grid's highlight already moved - push the old
                // selection back after the current binding update finishes.
                System.Windows.Application.Current?.Dispatcher.BeginInvoke(() =>
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SelectedSavedEntry))));
                return;
            }
            SetField(ref _selectedSavedEntry, value);
            if (value != null)
            {
                // _currentInfo must follow the display: Speichern and the
                // duplicate check act on it, so leaving it on the last READ
                // page would save edited notes against the wrong record.
                _currentInfo = value.Info;
                InvalidateDuplicateCheck();
                DisplayInfo(value.Info);
                NameText = value.Name;
                CommentText = value.Comment;
            }
            CommandManager.InvalidateRequerySuggested();
        }
    }

    bool _isEntriesPanelOpen;
    public bool IsEntriesPanelOpen
    {
        get => _isEntriesPanelOpen;
        set => SetField(ref _isEntriesPanelOpen, value);
    }

    public void NavigateToSelectedEntry()
    {
        if (SelectedSavedEntry != null)
        {
            // Prefer the page-specific link (ARCHION permalink) so a saved
            // entry reopens the exact page, not just the book.
            NavigateTo(SelectedSavedEntry.Info.EffectivePageUrl);
        }
    }

    public ICommand ToggleConnectionCommand { get; }
    public ICommand ReadCommand { get; }
    public ICommand SaveCommand { get; }
    public ICommand SaveImageCommand { get; }
    public ICommand OpenUrlCommand { get; }
    public ICommand CopySourceCommand { get; }
    public ICommand CopyCitationCommand { get; }
    public ICommand OpenSettingsCommand { get; }
    public ICommand HelpCommand { get; }
    public ICommand ToggleEntriesPanelCommand { get; }
    public ICommand DeleteEntryCommand { get; }
    public ICommand NavigateToEntryCommand { get; }
    public ICommand ExportEntriesCommand { get; }
    public ICommand ExportBibTexCommand { get; }
    public ICommand ClearAllEntriesCommand { get; }

    public event PropertyChangedEventHandler? PropertyChanged;

    public MainViewModel()
    {
        var formats = FormatSettingsStore.Load();
        _sourceFormats = formats.SourceFormats;
        _citationFormats = formats.CitationFormats;
        _selectedSourceFormat = formats.SelectedSource;
        _selectedCitationFormat = formats.SelectedCitation;

        foreach (var record in SavedEntryStore.Load())
        {
            SavedEntries.Add(new SavedEntry(record));
        }

        ToggleConnectionCommand = new RelayCommand(ToggleConnection);
        ReadCommand = new RelayCommand(ReadCurrentTab, () => _connection.IsConnected);
        // Save is blocked while record+notes exactly match an existing entry
        // (an accidental repeat click); the same page with different
        // name/comment stays saveable - several finds per page are normal.
        SaveCommand = new RelayCommand(SaveCurrentEntry,
            () => _currentInfo != null && !IsDuplicateOfSaved());
        SaveImageCommand = new RelayCommand(SaveImage, () => !string.IsNullOrEmpty(ImageUrlText));
        OpenUrlCommand = new RelayCommand(() => NavigateTo(UrlText), () => !string.IsNullOrEmpty(UrlText));
        OpenSettingsCommand = new RelayCommand(OpenSettings);
        HelpCommand = new RelayCommand(OpenManual);
        CopySourceCommand = new RelayCommand(
            () => Clipboard.SetText(SourceText), () => !string.IsNullOrEmpty(SourceText));
        CopyCitationCommand = new RelayCommand(
            () => Clipboard.SetText(PageCitationText), () => !string.IsNullOrEmpty(PageCitationText));
        ToggleEntriesPanelCommand = new RelayCommand(() => IsEntriesPanelOpen = !IsEntriesPanelOpen);
        // One command for all three delete affordances: the context menu and
        // the Delete key pass no parameter (delete the selected row), while
        // the inline trash button passes its OWN row - so it deletes that row
        // without first having to select it.
        DeleteEntryCommand = new RelayCommand<SavedEntry>(
            entry => DeleteEntry(entry ?? SelectedSavedEntry),
            entry => (entry ?? SelectedSavedEntry) != null);
        NavigateToEntryCommand = new RelayCommand(NavigateToSelectedEntry, () => SelectedSavedEntry != null);
        ExportEntriesCommand = new RelayCommand(ExportEntries, () => SavedEntries.Count > 0);
        ExportBibTexCommand = new RelayCommand(ExportBibTex, () => SavedEntries.Count > 0);
        ClearAllEntriesCommand = new RelayCommand(ClearAllEntries, () => SavedEntries.Count > 0);
    }

    // "Does the current record+notes exactly match a saved entry?" - the
    // scan is exact but O(entries) with record value-equality, and it sits
    // in SaveCommand.CanExecute, which CommandManager re-queries on
    // practically every UI event. So the result is CACHED and invalidated
    // at the few places its inputs (record, notes, entry list) change.
    // Deliberately not a plain dirty flag: the scan re-evaluates exactly,
    // so undoing an edit back to the saved text or deleting the matching
    // entry gives the right answer - a flag would get both wrong.
    bool? _isDuplicateCached;

    void InvalidateDuplicateCheck() => _isDuplicateCached = null;

    bool IsDuplicateOfSaved() => _isDuplicateCached ??= SavedEntries.Any(e =>
        e.Info == _currentInfo && e.Name == NameText.Trim() && e.Comment == CommentText.Trim());

    void DeleteEntry(SavedEntry? entry)
    {
        if (entry == null)
        {
            return;
        }
        // Was this the entry currently on display? (Selecting an entry makes
        // it the selected one - see SelectedSavedEntry.)
        bool wasDisplayed = ReferenceEquals(_selectedSavedEntry, entry);
        SavedEntries.Remove(entry);
        if (wasDisplayed)
        {
            // Drop the selection AND wipe the fields: otherwise the deleted
            // entry's notes stay in Name/Kommentar/Seite with no matching
            // saved entry, so the next list selection would pop the
            // discard-unsaved warning. Deleting a DIFFERENT row (via its
            // trash button) leaves the selection and display untouched.
            SetField(ref _selectedSavedEntry, null, nameof(SelectedSavedEntry));
            ClearDisplay();
        }
        InvalidateDuplicateCheck();
        PersistEntries();
    }

    // Bulk delete, behind an explicit confirmation - this throws away every
    // saved find, which can be years of research, so it must never happen on
    // a single stray click.
    void ClearAllEntries()
    {
        if (SavedEntries.Count == 0)
        {
            return;
        }
        if (MessageBox.Show(
                "Wirklich ALLE gespeicherten Einträge löschen? Dies kann nicht rückgängig gemacht werden.",
                "Alle Einträge löschen",
                MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK)
        {
            return;
        }
        SavedEntries.Clear();
        // If a saved entry was on display, wipe the fields too (its notes
        // would otherwise be orphaned); a fresh unsaved read stays untouched.
        if (_selectedSavedEntry != null)
        {
            SetField(ref _selectedSavedEntry, null, nameof(SelectedSavedEntry));
            ClearDisplay();
        }
        InvalidateDuplicateCheck();
        PersistEntries();
    }

    void ToggleConnection()
    {
        if (_connection.IsConnected)
        {
            _connection.Disconnect();
            SetDisconnectedStatus();
            CommandManager.InvalidateRequerySuggested();
            return;
        }

        var picker = new BrowserPickerWindow(_connection.ListRunningBrowsers())
        {
            Owner = System.Windows.Application.Current.MainWindow
        };
        if (picker.ShowDialog() == true && picker.Chosen != null)
        {
            _connection.Connect(picker.Chosen);
            SetConnectedStatus();
        }
        CommandManager.InvalidateRequerySuggested();
    }

    void SetConnectedStatus()
    {
        ConnectionStatusText = $"Verbunden mit {_connection.ConnectedLabel}";
        IsConnected = true;
        // The startup hint ("Nicht verbunden - ...") has done its job -
        // don't leave it sitting in the status bar after connecting.
        StatusText = "";
    }

    void SetDisconnectedStatus()
    {
        ConnectionStatusText = "Nicht verbunden";
        IsConnected = false;
        // Callers with a more specific message (read errors) overwrite this
        // right after.
        StatusText = "Nicht verbunden – Browser über das Steckersymbol wählen.";
    }

    // A new read wipes the display and the annotation fields - if the user
    // typed something (Name/Kommentar/Seite) that is not saved as an entry
    // yet, ask before discarding it.
    bool ConfirmDiscardUnsaved()
    {
        bool hasInput = NameText.Trim().Length > 0 || CommentText.Trim().Length > 0 ||
                        PageText.Trim().Length > 0;
        if (!hasInput || IsDuplicateOfSaved())
        {
            return true;
        }
        return MessageBox.Show(
            "Die Eingaben (Name/Kommentar/Seite) wurden nicht gespeichert und gehen beim Lesen verloren. Fortfahren?",
            "Ungespeicherte Eingaben",
            MessageBoxButton.OKCancel, MessageBoxImage.Warning) == MessageBoxResult.OK;
    }

    // Lesen always starts fresh: whatever the outcome, the previously shown
    // record must not survive. A failed read that leaves the old fields
    // standing next to its error message reads as "this page was read
    // successfully" to anyone not watching the status bar.
    void ClearDisplay()
    {
        _currentInfo = null;
        InvalidateDuplicateCheck();
        _settingFieldsFromInfo = true;
        try
        {
            DisplayedInfo = null;
            BistumText = "";
            ParishText = "";
            BookText = "";
            SignatureText = "";
            PageText = "";
            PageIdText = "";
            UrlText = "";
            ImageUrlText = "";
            SourceText = "";
            PageCitationText = "";
            NameText = "";
            CommentText = "";
        }
        finally
        {
            _settingFieldsFromInfo = false;
        }
    }

    async void ReadCurrentTab()
    {
        if (!ConfirmDiscardUnsaved())
        {
            return;
        }
        ClearDisplay();

        string? url = _connection.ReadActiveTabUrl();
        if (!_connection.IsConnected)
        {
            // ReadActiveTabUrl disconnects internally if the connected
            // browser process has exited - reflect that immediately rather
            // than leaving a stale "Connected to X" showing.
            SetDisconnectedStatus();
        }
        if (url == null)
        {
            StatusText = _connection.IsConnected
                ? "Browser-Adressleiste konnte nicht gelesen werden."
                : "Nicht verbunden – Browser über das Steckersymbol wählen.";
            CommandManager.InvalidateRequerySuggested();
            return;
        }

        MatriculaInfo? info = null;
        var uri = TryParseBrowserUrl(url);
        var extractor = uri is null ? null : _extractors.FirstOrDefault(e => e.CanHandle(uri));
        if (extractor is not null)
        {
            // The context bundles what a provider may need beyond the URL: the
            // logged-in tab title (ARCHION's data is there, not in a cookie-less
            // fetch) and an on-demand page-link lookup in the rendered page
            // (ARCHION's permalink). Most extractors use only the URL.
            var page = new PageContext(uri!, _connection.ReadActiveTabTitle(),
                                       _connection.FindPageLinkMatching);
            try
            {
                info = await extractor.GetInfoAsync(page);
            }
            catch (Exception ex)
            {
                StatusText = $"Fehler: {ex.Message}";
                CommandManager.InvalidateRequerySuggested();
                return;
            }
        }

        // Discard the page number parsed from the scan label - it rarely
        // matches the real handwritten page number, so the Seite field
        // starts empty on every read and only ever holds what the user
        // typed. No guessing.
        info = info is null ? null : info with { Page = null };
        _currentInfo = info;
        InvalidateDuplicateCheck();
        if (info != null)
        {
            StatusText = "";
            DisplayInfo(info);
        }
        else
        {
            StatusText = $"Keine unterstützte Kirchenbuch-Seite: {url}";
        }
        SetField(ref _selectedSavedEntry, null, nameof(SelectedSavedEntry));
        CommandManager.InvalidateRequerySuggested();
    }

    void SaveCurrentEntry()
    {
        if (_currentInfo == null)
        {
            return;
        }
        var record = new SavedRecord(
            Guid.NewGuid(), DateTime.Now, NameText.Trim(), CommentText.Trim(), _currentInfo);
        SavedEntries.Add(new SavedEntry(record));
        InvalidateDuplicateCheck();
        PersistEntries();
        // The just-saved state is now a duplicate - gray out Speichern.
        CommandManager.InvalidateRequerySuggested();
    }

    // Saved finds can represent years of research - a failed write must
    // never pass silently (unlike the best-effort format settings).
    void PersistEntries()
    {
        try
        {
            SavedEntryStore.Save(SavedEntries.Select(e => e.Record));
        }
        catch (Exception ex)
        {
            StatusText = $"Fehler beim Speichern der Einträge: {ex.Message}";
        }
    }

    // Exports the saved finds for the user's own long-term archive (CSV,
    // German-Excel-friendly - see EntryCsvExporter). Exported in the grid's
    // natural order (newest first is only a view sort; the file keeps the
    // stored order).
    void ExportEntries()
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            FileName = $"Kirchenbuch-Funde_{DateTime.Now:yyyy-MM-dd}.csv",
            Filter = "CSV-Datei|*.csv|Alle Dateien|*.*"
        };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        try
        {
            EntryCsvExporter.Export(
                SavedEntries.Select(e => e.Record), dialog.FileName,
                _selectedSourceFormat, _selectedCitationFormat);
            StatusText = $"{SavedEntries.Count} Einträge exportiert nach {dialog.FileName}";
        }
        catch (Exception ex)
        {
            StatusText = $"Fehler beim Exportieren: {ex.Message}";
        }
    }

    // The .bib is rendered with the user's "BibTeX" source format. If they
    // deleted it, offer to re-add the built-in catalog one (which can't be
    // lost) rather than silently guessing - and let them cancel.
    const string BibTexFormatName = "BibTeX";

    void ExportBibTex()
    {
        var bibFormat = _sourceFormats.FirstOrDefault(f => f.Name == BibTexFormatName);
        if (bibFormat is null)
        {
            if (MessageBox.Show(
                    "Es ist kein Quellenformat namens „BibTeX“ vorhanden.\n\n" +
                    "Soll das Standard-BibTeX-Format zu den Quellenformaten hinzugefügt werden?",
                    "BibTeX-Export", MessageBoxButton.OKCancel, MessageBoxImage.Question)
                != MessageBoxResult.OK)
            {
                return;
            }
            bibFormat = CitationStyleCatalog.SourceSeed.First(f => f.Name == BibTexFormatName);
            SourceFormats = _sourceFormats.Append(bibFormat).ToList();
            SaveFormats();
        }

        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            FileName = $"Kirchenbuch-Bibliothek_{DateTime.Now:yyyy-MM-dd}.bib",
            Filter = "BibTeX-Datei|*.bib|Alle Dateien|*.*"
        };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        try
        {
            BibTexExporter.Export(SavedEntries.Select(e => e.Record), dialog.FileName, bibFormat);
            StatusText = $"BibTeX-Bibliothek exportiert nach {dialog.FileName}";
        }
        catch (Exception ex)
        {
            StatusText = $"Fehler beim Exportieren: {ex.Message}";
        }
    }

    async void SaveImage()
    {
        if (string.IsNullOrEmpty(ImageUrlText))
        {
            return;
        }

        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            FileName = BuildImageFileName(),
            // The suggested filename already carries the right extension -
            // AddExtension's own guesswork just duplicates it (observed:
            // "...jpg.jpg") since it gets confused by the extra periods the
            // filename can legitimately contain (e.g. Matricula's own
            // "03. 00" page-label style).
            AddExtension = false,
            Filter = "Bilddateien|*.jpg;*.jpeg;*.png;*.gif;*.tif;*.tiff|Alle Dateien|*.*"
        };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        // Route the download through the provider the displayed page came
        // from (relevant for redisplayed saved entries: the stored page URL
        // identifies the provider, not any current app state).
        var extractor = _displayedInfo is null ? null : FindExtractor(_displayedInfo.Url);
        if (extractor is null)
        {
            StatusText = "Kein Anbieter für dieses Bild bekannt.";
            return;
        }

        try
        {
            byte[] bytes = await extractor.DownloadImageAsync(ImageUrlText);
            await File.WriteAllBytesAsync(dialog.FileName, bytes);
            StatusText = $"Bild gespeichert unter {dialog.FileName}";
        }
        catch (Exception ex)
        {
            StatusText = $"Fehler beim Speichern des Bildes: {ex.Message}";
        }
    }

    // Address bars display URLs scheme-elided ("data.matricula-online.eu/
    // ..."), which Uri.TryCreate rejects as Absolute - a browser quirk, not
    // a provider one, so it is normalized here before any extractor sees
    // the URL. Deliberately NOT a Contains("://") check: browsers also
    // display percent-encoding decoded, so a DFG-viewer URL carries a
    // nested "://" inside its tx_dlf[id] value even when its own scheme is
    // elided - only a scheme the parser accepts at the START counts.
    static Uri? TryParseBrowserUrl(string url)
    {
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri) &&
            (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
        {
            return uri;
        }
        return Uri.TryCreate("https://" + url, UriKind.Absolute, out uri) ? uri : null;
    }

    ICitationExtractor? FindExtractor(string url) =>
        TryParseBrowserUrl(url) is { } uri ? _extractors.FirstOrDefault(e => e.CanHandle(uri)) : null;

    // Suggested filename combines every field that identifies this exact
    // page, e.g. "Eichstätt, rk Bistum_Pollenfeld_Taufen 1670-1736_3-01_
    // Pollenfeld 03.00.jpg" - enough on its own to know what the file is
    // without needing to keep the app open.
    string BuildImageFileName()
    {
        string name = string.Join("_",
            new[] { BistumText, ParishText, BookText, SignatureText, PageIdText }
                .Where(s => !string.IsNullOrWhiteSpace(s)));
        foreach (char c in Path.GetInvalidFileNameChars())
        {
            name = name.Replace(c, '_');
        }
        return name + (TryGetUrlExtension(ImageUrlText) ?? ".jpg");
    }

    static string? TryGetUrlExtension(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return null;
        }
        string ext = Path.GetExtension(Uri.UnescapeDataString(uri.AbsolutePath));
        // Guard against treating a stray "." inside a decoded path segment
        // (e.g. Matricula's own "...03. 00.jpg" naming) as the real
        // extension - a real one is short and plain ASCII letters/digits.
        bool looksLikeExtension = ext.Length is > 1 and <= 6 &&
            ext.Skip(1).All(c => char.IsAsciiLetterOrDigit(c));
        return looksLikeExtension ? ext : null;
    }

    async void NavigateTo(string url) => await _connection.TryNavigateAsync(url);

    // The published user manual (GitHub Pages). Opened in the user's DEFAULT
    // browser via ShellExecute - independent of the on-demand connection to a
    // church-book browser.
    const string ManualUrl = "https://matrikelhelfer.niggl-schlagbauer.de/";

    void OpenManual()
    {
        try
        {
            System.Diagnostics.Process.Start(
                new System.Diagnostics.ProcessStartInfo(ManualUrl) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            StatusText = $"Handbuch konnte nicht geöffnet werden: {ex.Message}";
        }
    }

    void OpenSettings()
    {
        var settings = new SettingsWindow(CurrentFormatSettings())
        {
            Owner = System.Windows.Application.Current.MainWindow
        };
        if (settings.ShowDialog() != true)
        {
            return;
        }
        var result = settings.Result;
        SourceFormats = result.SourceFormats;
        CitationFormats = result.CitationFormats;
        // The selection setters re-render their field and save - but they
        // no-op when the active format came back unchanged, so save once
        // more to also catch list-only edits.
        SelectedSourceFormat = result.SelectedSource;
        SelectedCitationFormat = result.SelectedCitation;
        SaveFormats();
    }

    FormatSettings CurrentFormatSettings() => new()
    {
        SourceFormats = _sourceFormats.ToList(),
        SelectedSource = _selectedSourceFormat,
        CitationFormats = _citationFormats.ToList(),
        SelectedCitation = _selectedCitationFormat,
    };

    void SaveFormats() => FormatSettingsStore.Save(CurrentFormatSettings());

    // Guards PageText's setter against re-applying an "override" while the
    // fields are being populated FROM a record (as opposed to a user edit).
    bool _settingFieldsFromInfo;

    void DisplayInfo(MatriculaInfo info)
    {
        _settingFieldsFromInfo = true;
        try
        {
            DisplayedInfo = info;
            BistumText = info.Bistum;
            ParishText = info.Pfarrei;
            BookText = info.BookLabel;
            SignatureText = info.Signatur;
            PageText = info.Page ?? "";
            PageIdText = info.ScanLabel;
            // The page link prefers a provider-supplied page-specific URL
            // (ARCHION's permalink) over the address-bar URL (which there is
            // only the book); for Matricula/DFG they are the same.
            UrlText = info.EffectivePageUrl;
            ImageUrlText = info.ImageUrl;
            SourceText = CitationTemplateEngine.Render(_selectedSourceFormat, info);
            PageCitationText = CitationTemplateEngine.Render(_selectedCitationFormat, info);
        }
        finally
        {
            _settingFieldsFromInfo = false;
        }
    }

    void ApplyPageOverride(string value)
    {
        if (_settingFieldsFromInfo || DisplayedInfo == null)
        {
            return;
        }
        // An emptied field means "page unknown" - {Seite} then renders
        // empty (never the scan number; see CitationTemplateEngine).
        string? page = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        DisplayedInfo = DisplayedInfo with { Page = page };
        if (_currentInfo != null)
        {
            _currentInfo = _currentInfo with { Page = page };
        }
        InvalidateDuplicateCheck();
        SourceText = CitationTemplateEngine.Render(_selectedSourceFormat, DisplayedInfo);
        PageCitationText = CitationTemplateEngine.Render(_selectedCitationFormat, DisplayedInfo);
    }

    void SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (Equals(field, value))
        {
            return;
        }
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
