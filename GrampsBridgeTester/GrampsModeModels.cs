using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using GrampsBridge;

namespace GrampsBridgeTester;

public abstract class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected void OnChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return false;
        field = value;
        OnChanged(name);
        return true;
    }
}

public sealed class RelayCommand<T>(Action<T> execute, Func<T, bool>? canExecute = null)
    : ICommand
{
    public event EventHandler? CanExecuteChanged
    {
        add => CommandManager.RequerySuggested += value;
        remove => CommandManager.RequerySuggested -= value;
    }

    public bool CanExecute(object? parameter) =>
        parameter is T value && (canExecute?.Invoke(value) ?? true);

    public void Execute(object? parameter)
    {
        if (parameter is T value)
            execute(value);
    }
}

/// <summary>One box in the walkable tree. Identity-keyed by Handle so
/// post-upload refreshes update in place (spec 7.3: no view jump).</summary>
public sealed class PersonBoxVM(string handle) : ObservableObject
{
    public string Handle { get; } = handle;

    /// <summary>"Neu" slot: keeps the tree skeleton stable where a person
    /// is missing. Display-only until v2 (virtual persons).</summary>
    public bool IsPlaceholder { get; init; }

    /// <summary>Center couple boxes render 1.5x the surrounding ones.</summary>
    private bool _isLarge;
    public bool IsLarge { get => _isLarge; set => Set(ref _isLarge, value); }

    public static PersonBoxVM Placeholder(bool large = false) =>
        new("") { IsPlaceholder = true, Name = "Neu", IsLarge = large };

    private string _name = "";
    public string Name { get => _name; set => Set(ref _name, value); }

    private string _grampsId = "";
    public string GrampsId { get => _grampsId; set => Set(ref _grampsId, value); }

    private string _birthText = "";
    public string BirthText { get => _birthText; set => Set(ref _birthText, value); }

    private string _deathText = "";
    public string DeathText { get => _deathText; set => Set(ref _deathText, value); }

    private bool _isCenter;
    public bool IsCenter { get => _isCenter; set => Set(ref _isCenter, value); }

    private bool _isDraftTarget;
    public bool IsDraftTarget { get => _isDraftTarget; set => Set(ref _isDraftTarget, value); }

    private int _pendingCount;
    public int PendingCount
    {
        get => _pendingCount;
        set { if (Set(ref _pendingCount, value)) OnChanged(nameof(Badges)); }
    }

    private int _uploadedCount;
    public int UploadedCount
    {
        get => _uploadedCount;
        set { if (Set(ref _uploadedCount, value)) OnChanged(nameof(Badges)); }
    }

    public string Badges =>
        (PendingCount > 0 ? $"○ {PendingCount} ausstehend  " : "")
        + (UploadedCount > 0 ? $"● {UploadedCount} in Gramps" : "");

    private string _toolTipText = "";
    public string ToolTipText { get => _toolTipText; set => Set(ref _toolTipText, value); }

    /// <summary>shortPlace: only the lowest hierarchy level ("Freilassing"
    /// instead of "Freilassing, Traunstein, Bayern, Deutschland").</summary>
    private static string LifeLine(string prefix, LifeEvent? life, bool shortPlace)
    {
        if (life is null)
            return "";
        var parts = new List<string>();
        if (life.DateText is { Length: > 0 } date)
            parts.Add(date);
        var place = life.Place;
        if (place is { Length: > 0 })
        {
            if (shortPlace)
                place = place.Split(',')[0].Trim();
            parts.Add($"({place})");
        }
        return parts.Count == 0 ? "" : prefix + " " + string.Join(" ", parts);
    }

    /// <summary>Box line: year only, lowest place level, no parentheses —
    /// "* 1745 Unterhausen". The tooltip keeps the full form.</summary>
    private static string CompactLifeLine(string prefix, LifeEvent? life)
    {
        if (life is null)
            return "";
        var parts = new List<string>();
        var year = life.SortYear?.ToString() ?? life.DateText;
        if (year is { Length: > 0 })
            parts.Add(year);
        var place = life.Place?.Split(',')[0].Trim();
        if (place is { Length: > 0 })
            parts.Add(place);
        return parts.Count == 0 ? "" : prefix + " " + string.Join(" ", parts);
    }

    public void UpdateFrom(PersonBrief brief)
    {
        Name = brief.PrimaryName ?? "(ohne Name)";
        GrampsId = brief.GrampsId ?? "";
        BirthText = CompactLifeLine("*", brief.Birth);
        DeathText = CompactLifeLine("+", brief.Death);
        ToolTipText = string.Join("\n", new[]
        {
            Name + (GrampsId.Length > 0 ? $"  [{GrampsId}]" : ""),
            LifeLine("*", brief.Birth, shortPlace: false),
            LifeLine("+", brief.Death, shortPlace: false),
        }.Where(line => line.Length > 0));
    }
}

/// <summary>One row in the facts/events list of the center person.</summary>
public sealed class FactRowVM(string handle) : ObservableObject
{
    public string Handle { get; } = handle;

    private string _label = "";
    public string Label { get => _label; set => Set(ref _label, value); }

    private string _scope = "person";
    public string Scope { get => _scope; set => Set(ref _scope, value); }

    public string? FamilyHandle { get; set; }

    private int _citationCount;
    public int CitationCount { get => _citationCount; set => Set(ref _citationCount, value); }

    private bool _isDraftTarget;
    public bool IsDraftTarget { get => _isDraftTarget; set => Set(ref _isDraftTarget, value); }

    private int _pendingCount;
    public int PendingCount
    {
        get => _pendingCount;
        set { if (Set(ref _pendingCount, value)) OnChanged(nameof(Badges)); }
    }

    private int _uploadedCount;
    public int UploadedCount
    {
        get => _uploadedCount;
        set { if (Set(ref _uploadedCount, value)) OnChanged(nameof(Badges)); }
    }

    public string Badges =>
        (PendingCount > 0 ? $"○{PendingCount} " : "")
        + (UploadedCount > 0 ? $"●{UploadedCount}" : "");

    public void UpdateFrom(PersonEvent evt)
    {
        var shortPlace = evt.Place?.Split(',')[0].Trim();
        Label = evt.Type
            + (evt.DateText is { } date ? " " + date : "")
            + (shortPlace is { Length: > 0 } ? $" ({shortPlace})" : "")
            + (evt.Scope == "family" ? "  [Familie]" : "");
        Scope = evt.Scope ?? "person";
        FamilyHandle = evt.FamilyHandle;
        CitationCount = evt.CitationCount;
    }
}

public sealed class TargetRefVM
{
    public required string Kind { get; init; }      // person | event
    public required string Handle { get; init; }
    public required string Label { get; init; }
}

public sealed class NewEventVM
{
    public required string EventType { get; init; }
    public required string OwnerKind { get; init; }  // person | family
    public required string OwnerHandle { get; init; }
    public required string OwnerLabel { get; init; }
    public string? Description { get; init; }
    public string Label => $"{EventType} (neu, {OwnerLabel})";
}

public enum AssignmentStatus { Pending, Uploaded, Failed }

/// <summary>A staged "find X evidences targets Y" entry — the local
/// queue of spec 7.3. Snapshot of the find fields at assignment time.</summary>
public sealed class AssignmentVM : ObservableObject
{
    public required string RepoName { get; init; }
    public required string RepoUrl { get; init; }
    public required string SourceTitle { get; init; }
    public required string SourceAuthor { get; init; }
    public required string SourceKey { get; init; }
    public required string CallNumber { get; init; }
    public required string Page { get; init; }
    public required string DateText { get; init; }
    public required string Confidence { get; init; }
    public required string Permalink { get; init; }
    public required string NoteText { get; init; }
    public required bool CopyLinkToPersons { get; init; }
    public List<TargetRefVM> Targets { get; init; } = [];
    public NewEventVM? NewEvent { get; init; }

    private AssignmentStatus _status = AssignmentStatus.Pending;
    public AssignmentStatus Status
    {
        get => _status;
        set { if (Set(ref _status, value)) OnChanged(nameof(StatusText)); }
    }

    private string? _error;
    public string? Error
    {
        get => _error;
        set { if (Set(ref _error, value)) OnChanged(nameof(StatusText)); }
    }

    public string? CitationId { get; set; }
    public string? CreatedEventHandle { get; set; }

    public string StatusText => Status switch
    {
        AssignmentStatus.Uploaded => $"● in Gramps ({CitationId})",
        AssignmentStatus.Failed => "✕ " + (Error ?? "Fehler"),
        _ => "○ ausstehend",
    };

    public string Summary
    {
        get
        {
            var targets = Targets.Select(t => t.Label).ToList();
            if (NewEvent is not null)
                targets.Add(NewEvent.Label);
            return $"{Page} → {string.Join(", ", targets)}";
        }
    }
}
