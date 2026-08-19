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
    /// is missing. Clicking one creates a virtual person (spec 7.3 v2).</summary>
    public bool IsPlaceholder { get; init; }

    /// <summary>Virtual person: a pending create-person change entry
    /// shown as a tree box (Handle = "new:" + entry id). Clicking it
    /// adds events to the new person; navigation needs the upload.</summary>
    private bool _isVirtual;
    public bool IsVirtual { get => _isVirtual; set => Set(ref _isVirtual, value); }

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

    private int _pendingCount;
    public int PendingCount
    {
        get => _pendingCount;
        set { if (Set(ref _pendingCount, value)) OnChanged(nameof(Badges)); }
    }

    public string Badges =>
        PendingCount > 0 ? $"○ {PendingCount} Änderung(en)" : "";

    private string _toolTipText = "";
    public string ToolTipText { get => _toolTipText; set => Set(ref _toolTipText, value); }

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

    private static string LifeLine(string prefix, LifeEvent? life)
    {
        if (life is null)
            return "";
        var parts = new List<string>();
        if (life.DateText is { Length: > 0 } date)
            parts.Add(date);
        if (life.Place is { Length: > 0 } place)
            parts.Add($"({place})");
        return parts.Count == 0 ? "" : prefix + " " + string.Join(" ", parts);
    }

    /// <summary>Real and virtual persons come through the same graph
    /// node — the box only varies the life lines.</summary>
    public void UpdateFromNode(TreePerson node)
    {
        Name = node.DisplayName is { Length: > 0 } name ? name : "(ohne Name)";
        GrampsId = node.GrampsId ?? "";
        IsVirtual = node.IsVirtual;
        if (node.IsVirtual)
        {
            BirthText = "(neu)";
            DeathText = "";
            ToolTipText = Name + "\nNoch nicht in Gramps — wird beim "
                + "Upload angelegt. Klick: auswählen und weiterarbeiten "
                + "wie bei jeder anderen Person.";
            return;
        }
        BirthText = CompactLifeLine("*", node.Birth);
        DeathText = CompactLifeLine("+", node.Death);
        ToolTipText = string.Join("\n", new[]
        {
            Name + (GrampsId.Length > 0 ? $"  [{GrampsId}]" : ""),
            LifeLine("*", node.Birth),
            LifeLine("+", node.Death),
        }.Where(line => line.Length > 0));
    }
}

/// <summary>One row in the facts/events list: a real Gramps event or a
/// pending one produced by a create-event change entry. Deliberately no
/// person-object row: a church record always evidences a fact, never
/// the person itself (decision 2026-08-18, see spec 7.3).</summary>
public sealed class FactRowVM(string handle) : ObservableObject
{
    public string Handle { get; } = handle;   // event handle | change-entry id

    private string _label = "";
    public string Label { get => _label; set => Set(ref _label, value); }

    private string _scope = "person";
    public string Scope { get => _scope; set => Set(ref _scope, value); }

    public string? FamilyHandle { get; set; }

    private int _citationCount;
    public int CitationCount { get => _citationCount; set => Set(ref _citationCount, value); }

    private bool _isPendingNew;
    public bool IsPendingNew { get => _isPendingNew; set => Set(ref _isPendingNew, value); }

    /// <summary>Citations already on this row in Gramps — the link view
    /// draws its solid lines and locked checkboxes from these.</summary>
    public List<CitationRef> Citations { get; private set; } = [];

    // -- link-view state (set by MainViewModel.RefreshLinkView) -------

    private bool _isSelected;
    public bool IsSelected { get => _isSelected; set => Set(ref _isSelected, value); }

    private bool _isAssignSubject;
    public bool IsAssignSubject { get => _isAssignSubject; set => Set(ref _isAssignSubject, value); }

    private bool _showCheckBox;
    public bool ShowCheckBox { get => _showCheckBox; set => Set(ref _showCheckBox, value); }

    private bool _isChecked;
    public bool IsChecked { get => _isChecked; set => Set(ref _isChecked, value); }

    private bool _isCheckEnabled = true;
    public bool IsCheckEnabled { get => _isCheckEnabled; set => Set(ref _isCheckEnabled, value); }

    private int _pendingCount;
    public int PendingCount
    {
        get => _pendingCount;
        set { if (Set(ref _pendingCount, value)) OnChanged(nameof(Badges)); }
    }

    public string Badges => PendingCount > 0 ? $"○{PendingCount}" : "";

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
        Citations = evt.Citations ?? [];
        IsPendingNew = false;
    }

    public void UpdateFromPending(ChangeEntry entry)
    {
        var isFamily = entry.OwnerKind is "family" or "pending-family";
        Label = $"(neu) {entry.EventTypeLabel ?? entry.EventType}"
            + (entry.EventDateText is { Length: > 0 } date ? " " + date : "")
            + (isFamily ? "  [Familie]" : "");
        Scope = isFamily ? "family" : "person";
        FamilyHandle = isFamily ? entry.OwnerHandle : null;
        CitationCount = 0;
        Citations = [];
        IsPendingNew = true;
    }
}

/// <summary>One card in the sources column: an existing citation of the
/// displayed person (keyed by citation handle) or the current find
/// (key "find"). Target sets are fact-row keys; recomputed by
/// MainViewModel.RefreshLinkView.</summary>
public sealed class SourceCardVM(string key) : ObservableObject
{
    public string Key { get; } = key;          // citation handle | "find"
    public bool IsFind => Key == "find";

    private string _title = "";
    public string Title { get => _title; set => Set(ref _title, value); }

    private string _page = "";
    public string Page { get => _page; set => Set(ref _page, value); }

    private string _toolTipText = "";
    public string ToolTipText { get => _toolTipText; set => Set(ref _toolTipText, value); }

    /// <summary>Fact rows already carrying this citation in Gramps
    /// (always empty for the find card). Locked in assign mode — the
    /// bridge deliberately cannot detach citations (spec 2.2).</summary>
    public HashSet<string> ExistingTargets { get; } = [];

    /// <summary>Fact rows targeted by change-list entries.</summary>
    public HashSet<string> PendingTargets { get; } = [];

    private bool _isSelected;
    public bool IsSelected { get => _isSelected; set => Set(ref _isSelected, value); }

    private bool _isAssignSubject;
    public bool IsAssignSubject { get => _isAssignSubject; set => Set(ref _isAssignSubject, value); }

    private bool _showCheckBox;
    public bool ShowCheckBox { get => _showCheckBox; set => Set(ref _showCheckBox, value); }

    private bool _isChecked;
    public bool IsChecked { get => _isChecked; set => Set(ref _isChecked, value); }

    private bool _isCheckEnabled = true;
    public bool IsCheckEnabled { get => _isCheckEnabled; set => Set(ref _isCheckEnabled, value); }
}

/// <summary>Frozen copy of the find fields at the moment of a change.
/// GroupKey identifies "same record" for upload-time coalescing into one
/// shared citation (in the real app this is simply the Finding id).</summary>
public sealed class FindSnapshot
{
    public required string RepoName { get; init; }
    public required string RepoUrl { get; init; }
    public required string SourceTitle { get; init; }
    public required string SourceAuthor { get; init; }
    public required string Abbrev { get; init; }
    public required string SourceKey { get; init; }
    public required string CallNumber { get; init; }
    public required string Page { get; init; }
    public required string DateText { get; init; }
    public required string Confidence { get; init; }
    public required string Permalink { get; init; }
    public required string NoteText { get; init; }
    public required bool CopyLinkToPersons { get; init; }

    public string GroupKey =>
        string.Join("|", SourceKey, Page, DateText, Permalink, NoteText, Confidence);
}

/// <summary>Families ComboBox item: the graph node plus its rendered
/// label from the center's perspective.</summary>
public sealed record TreeFamilyChoice(TreeFamily Family, string Display);

/// <summary>One entry of the grouped event-type ComboBox, from the
/// bridge's /event-types catalog (= the Gramps event editor's list).
/// Xml goes to the API, Label to the user, Group to the view's
/// PropertyGroupDescription.</summary>
public sealed record EventTypeChoice(
    string Group, string Xml, string Label, bool IsFamily);

public enum ChangeKind { AttachCitation, CreateEvent, AttachExisting, CreatePerson }

/// <summary>One entry of the change list ("Änderungsliste"): a recorded
/// user action, executed at upload. DependsOnId links an entry to the
/// entry producing its target (e.g. citation on a pending event) — the
/// dependency tree doubles as the display tree, and deletion cascades
/// along it.</summary>
public sealed class ChangeEntry : ObservableObject
{
    // settable so a CreatePerson entry can use its own id as EntityKey
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public required ChangeKind Kind { get; init; }
    public FindSnapshot? Find { get; init; }             // null for AttachExisting
    public required string EntityKey { get; init; }      // person/family handle
    public required string EntityLabel { get; init; }
    public string? DependsOnId { get; init; }

    // AttachCitation / AttachExisting
    public string? TargetKind { get; init; }             // person | event | pending-event
    public string? TargetHandle { get; init; }           // handle | change-entry id
    public string? TargetLabel { get; init; }

    // AttachExisting: which existing Gramps citation to attach
    public string? CitationHandle { get; init; }
    public string? SourceLabel { get; init; }

    // CreatePerson: display fields only — the linkage (child/spouse/
    // parent, which family) lives in the tree graph, keyed by this
    // entry's id ("new:" + Id is the node id)
    public string? NewGiven { get; init; }
    public string? NewSurname { get; init; }
    public string? NewGender { get; init; }
    public string? RoleLabel { get; init; }              // Vater/Mutter/Partner/Kind

    // CreateEvent — EventType is the locale-independent XML name for
    // the API, EventTypeLabel the localized display text. OwnerKind
    // "pending-person"/"pending-family" references a not-yet-uploaded
    // graph node; the upload REWRITES owner to the real handle the
    // moment the node materializes, so retries after partial failures
    // behave like ordinary events.
    public string? EventType { get; init; }
    public string? EventTypeLabel { get; init; }
    // the EVENT's own date, user-entered incl. qualifier — NOT the
    // record's date (that one lives on the citation via Find)
    public DateSpec? EventDate { get; init; }
    public string? EventDateText { get; init; }          // display, e.g. "vor 1757"
    public string? OwnerKind { get; set; }               // person | family | pending-person | pending-family
    public string? OwnerHandle { get; set; }
    public string? EventDescription { get; init; }

    private string? _error;
    public string? Error
    {
        get => _error;
        set { if (Set(ref _error, value)) OnChanged(nameof(HasError)); }
    }

    public bool HasError => Error is not null;

    public string DisplayText => Kind switch
    {
        ChangeKind.CreateEvent =>
            $"Neues Ereignis: {EventTypeLabel ?? EventType}"
            + (EventDateText is { Length: > 0 } ? " " + EventDateText : "")
            + $" — mit Zitat {Find!.Page}",
        ChangeKind.AttachExisting =>
            $"Vorhandenes Zitat „{SourceLabel}“ → {TargetLabel}",
        ChangeKind.CreatePerson =>
            $"Neue Person: {NewGiven} {NewSurname} ({RoleLabel ?? "?"})",
        _ => $"Zitat {Find!.Page} → {TargetLabel}",
    };
}

public sealed class ChangeNodeVM(ChangeEntry entry)
{
    public ChangeEntry Entry { get; } = entry;
    public List<ChangeNodeVM> Children { get; } = [];
}

public sealed class ChangeGroupVM
{
    public required string EntityKey { get; init; }
    public required string EntityLabel { get; init; }
    public List<ChangeNodeVM> Children { get; } = [];
}
