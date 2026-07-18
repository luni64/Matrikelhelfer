using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using Matrikelhelfer.Models;
using Matrikelhelfer.Services;

namespace Matrikelhelfer.ViewModels;

// One editable format in the editor list. A mutable wrapper around the
// immutable CitationStyle record, so the Name/Template TextBoxes can bind
// two-way and the ListBox entry renames itself while the user types.
class FormatItem : INotifyPropertyChanged
{
    string _name;
    string _template;
    CitationTemplateEngine.DateStyle _dateStyle;

    public FormatItem(string name, string template, string dateFormat)
    {
        _name = name;
        _template = template;
        _dateStyle = CitationTemplateEngine.DateStyleById(dateFormat);
    }

    public string Name
    {
        get => _name;
        set => SetField(ref _name, value);
    }

    public string Template
    {
        get => _template;
        set => SetField(ref _template, value);
    }

    // The DateStyle object itself (not just its id) so the editor's
    // dropdown can bind SelectedItem directly.
    public CitationTemplateEngine.DateStyle DateStyle
    {
        get => _dateStyle;
        set => SetField(ref _dateStyle, value);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

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

// One switchable target in the editor - "which field do these formats
// render into" (Quellenangabe, Zitatangabe, maybe more later). Holds that
// target's format list and its own selection, which is the ACTIVE format
// applied to the field after OK - so switching targets in the dropdown
// never loses which format is active where.
class FormatTargetViewModel : INotifyPropertyChanged
{
    public string DisplayName { get; }
    public ObservableCollection<FormatItem> Formats { get; }

    FormatItem? _selected;
    public FormatItem? Selected
    {
        get => _selected;
        set => SetField(ref _selected, value);
    }

    public FormatTargetViewModel(string displayName, ObservableCollection<FormatItem> formats)
    {
        DisplayName = displayName;
        Formats = formats;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

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

// Backs the format editor in SettingsWindow. Works on a mutable copy of the
// caller's formats (OK/Cancel dialog semantics - the caller only reads the
// result back after ShowDialog() returned true). A dropdown switches the
// visible target (source/citation formats); the single list and editor pane
// always show the selected target's formats and selection.
class FormatEditorViewModel : INotifyPropertyChanged
{
    // Fixed sample record for the live preview, so the editor renders
    // something realistic even when no Matricula page has been read yet.
    // Values mirror a real Pollenfeld baptism-register page.
    static readonly MatriculaInfo SampleInfo = new(
        Land: "Deutschland",
        Bistum: "Eichstätt, rk Bistum",
        Pfarrei: "Pollenfeld",
        Buchtyp: "Taufen",
        DatumVon: "1. Januar 1670",
        DatumBis: "31. Dezember 1736",
        Signatur: "3-01",
        SignaturPfarrei: "",
        SignaturBuch: "3-01",
        Scan: 8,
        Page: "007",
        ScanLabel: "Pollenfeld 01. 007",
        Url: "https://data.matricula-online.eu/de/deutschland/eichstaett/pollenfeld/3-01/?pg=8",
        ImageUrl: "https://data.matricula-online.eu/images/eichstaett/pollenfeld/3-01/Pollenfeld_3-01_007.jpg");

    public IReadOnlyList<FormatTargetViewModel> Targets { get; }

    FormatTargetViewModel _selectedTarget;
    public FormatTargetViewModel SelectedTarget
    {
        get => _selectedTarget;
        set
        {
            SetField(ref _selectedTarget, value);
            UpdatePreview();
        }
    }

    // Placeholder names + tooltip descriptions for the clickable chips,
    // grouped into rows by topic - the braces are only added on insertion,
    // not shown on the chip.
    public IReadOnlyList<IReadOnlyList<CitationTemplateEngine.Placeholder>> PlaceholderGroups { get; } =
        CitationTemplateEngine.PlaceholderGroups;

    // The selectable date renderings for the editor's Datumsformat dropdown.
    public IReadOnlyList<CitationTemplateEngine.DateStyle> DateStyles { get; } =
        CitationTemplateEngine.DateStyles;

    string _previewText = "";
    public string PreviewText
    {
        get => _previewText;
        private set => SetField(ref _previewText, value);
    }

    public ICommand AddCommand { get; }
    public ICommand DeleteCommand { get; }

    public event PropertyChangedEventHandler? PropertyChanged;

    public FormatEditorViewModel(FormatSettings settings)
    {
        var sources = new FormatTargetViewModel("Quellenformate", Wrap(settings.SourceFormats));
        sources.Selected = sources.Formats.FirstOrDefault(f => f.Name == settings.SelectedSource.Name)
            ?? sources.Formats.FirstOrDefault();
        var citations = new FormatTargetViewModel("Zitatformate", Wrap(settings.CitationFormats));
        citations.Selected = citations.Formats.FirstOrDefault(f => f.Name == settings.SelectedCitation.Name)
            ?? citations.Formats.FirstOrDefault();

        Targets = new[] { sources, citations };
        _selectedTarget = sources;
        foreach (var target in Targets)
        {
            target.PropertyChanged += OnTargetChanged;
        }

        AddCommand = new RelayCommand(Add);
        // Never delete the last format of a target - its field always needs one.
        DeleteCommand = new RelayCommand(Delete,
            () => SelectedTarget.Selected != null && SelectedTarget.Formats.Count > 1);

        UpdatePreview();
    }

    ObservableCollection<FormatItem> Wrap(IEnumerable<CitationStyle> styles)
    {
        var list = new ObservableCollection<FormatItem>(
            styles.Select(s => new FormatItem(s.Name, s.Template, s.DateFormat)));
        foreach (var item in list)
        {
            item.PropertyChanged += OnItemChanged;
        }
        return list;
    }

    void OnTargetChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (ReferenceEquals(sender, SelectedTarget) && e.PropertyName == nameof(FormatTargetViewModel.Selected))
        {
            UpdatePreview();
        }
    }

    void OnItemChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (ReferenceEquals(sender, SelectedTarget.Selected) &&
            e.PropertyName is nameof(FormatItem.Template) or nameof(FormatItem.DateStyle))
        {
            UpdatePreview();
        }
    }

    void Add()
    {
        // Start from the target's selected template rather than empty - new
        // formats are usually variants of an existing one.
        var item = new FormatItem("Neues Format",
            SelectedTarget.Selected?.Template ?? "",
            SelectedTarget.Selected?.DateStyle.Id ?? "original");
        item.PropertyChanged += OnItemChanged;
        SelectedTarget.Formats.Add(item);
        SelectedTarget.Selected = item;
    }

    void Delete()
    {
        var formats = SelectedTarget.Formats;
        var selected = SelectedTarget.Selected;
        if (selected == null || formats.Count <= 1)
        {
            return;
        }
        int index = formats.IndexOf(selected);
        selected.PropertyChanged -= OnItemChanged;
        formats.RemoveAt(index);
        SelectedTarget.Selected = formats[Math.Min(index, formats.Count - 1)];
    }

    void UpdatePreview()
    {
        PreviewText = SelectedTarget.Selected != null
            ? CitationTemplateEngine.Render(ToStyle(SelectedTarget.Selected), SampleInfo)
            : "";
    }

    public FormatSettings ToSettings() => new()
    {
        SourceFormats = Targets[0].Formats.Select(ToStyle).ToList(),
        SelectedSource = ToStyle(Targets[0].Selected ?? Targets[0].Formats[0]),
        CitationFormats = Targets[1].Formats.Select(ToStyle).ToList(),
        SelectedCitation = ToStyle(Targets[1].Selected ?? Targets[1].Formats[0]),
    };

    static CitationStyle ToStyle(FormatItem item) => new(item.Name, item.Template, item.DateStyle.Id);

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
