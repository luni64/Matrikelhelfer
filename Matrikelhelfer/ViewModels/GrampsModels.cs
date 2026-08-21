using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using GrampsBridge;
using Matrikelhelfer.Services;

namespace Matrikelhelfer.ViewModels;

// View models of the Gramps-Modus view, ported from the GrampsBridgeTester
// sandbox (frozen). Key difference to the tester: a change entry does not
// carry a snapshot of live form fields - it REFERENCES the saved Finding
// (FindingId), which is the stable identity the spec asks for; the actual
// source/citation payload is resolved from the library at upload time.

/// <summary>Small INPC base for the in-place-updated items below (the
/// main VMs use their own SetField; these items are numerous and their
/// own hierarchy).</summary>
abstract class GrampsObservable : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected void OnChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }
        field = value;
        OnChanged(name);
        return true;
    }
}

/// <summary>One box in the walkable tree. Identity-keyed by node id so
/// post-upload refreshes update in place (spec 7.3: no view jump).</summary>
sealed class PersonBoxVM(string id) : GrampsObservable
{
    public string Id { get; } = id;

    /// <summary>"Neu" slot: keeps the tree skeleton stable where a person
    /// is missing. Clicking one creates a virtual person.</summary>
    public bool IsPlaceholder { get; init; }

    bool _isVirtual;
    public bool IsVirtual { get => _isVirtual; set => Set(ref _isVirtual, value); }

    bool _isLarge;
    public bool IsLarge { get => _isLarge; set => Set(ref _isLarge, value); }

    public static PersonBoxVM Placeholder(bool large = false) =>
        new("") { IsPlaceholder = true, Name = "Neu", IsLarge = large };

    string _name = "";
    public string Name { get => _name; set => Set(ref _name, value); }

    string _birthText = "";
    public string BirthText { get => _birthText; set => Set(ref _birthText, value); }

    string _deathText = "";
    public string DeathText { get => _deathText; set => Set(ref _deathText, value); }

    // Small (parent/child) boxes show ONE life line: "*1800", "+1880"
    // or "1800–1880" depending on which dates exist; details live in
    // the tooltip. The large couple boxes keep the two full lines.
    string _spanText = "";
    public string SpanText { get => _spanText; set => Set(ref _spanText, value); }

    bool _isCenter;
    public bool IsCenter { get => _isCenter; set => Set(ref _isCenter, value); }

    string _toolTipText = "";
    public string ToolTipText { get => _toolTipText; set => Set(ref _toolTipText, value); }

    /// <summary>Box line: year only, lowest place level — "* 1745
    /// Unterhausen". The tooltip keeps the full form.</summary>
    static string CompactLifeLine(string prefix, LifeEvent? life)
    {
        if (life is null)
        {
            return "";
        }
        var parts = new List<string>();
        string? year = life.SortYear?.ToString() ?? life.DateText;
        if (year is { Length: > 0 })
        {
            parts.Add(year);
        }
        string? place = life.Place?.Split(',')[0].Trim();
        if (place is { Length: > 0 })
        {
            parts.Add(place);
        }
        return parts.Count == 0 ? "" : prefix + " " + string.Join(" ", parts);
    }

    static string LifeLine(string prefix, LifeEvent? life)
    {
        if (life is null)
        {
            return "";
        }
        var parts = new List<string>();
        if (life.DateText is { Length: > 0 } date)
        {
            parts.Add(date);
        }
        if (life.Place is { Length: > 0 } place)
        {
            parts.Add($"({place})");
        }
        return parts.Count == 0 ? "" : prefix + " " + string.Join(" ", parts);
    }

    /// <summary>Progressive name shortening for the fixed-width boxes:
    /// a plain right-ellipsis eats the GIVEN names, which are the
    /// distinguishing part inside a family where surnames repeat.
    /// Steps until the character budget fits: middle givens to initials
    /// ("Schlagbauer, Paula F. C.") -> drop middles -> first given as
    /// initial ("Schlagbauer, P."); anything still longer is left to
    /// TextTrimming. Handles Gramps' "Surname, Givens" display format
    /// and plain "Givens Surname". The tooltip keeps the full name.</summary>
    static string ShortenName(string name, int budget)
    {
        if (name.Length <= budget)
        {
            return name;
        }
        int comma = name.IndexOf(',');
        bool surnameFirst = comma > 0;
        string surname, givens;
        if (surnameFirst)
        {
            surname = name[..comma].Trim();
            givens = name[(comma + 1)..].Trim();
        }
        else
        {
            int lastSpace = name.LastIndexOf(' ');
            if (lastSpace < 0)
            {
                return name;
            }
            surname = name[(lastSpace + 1)..];
            givens = name[..lastSpace].Trim();
        }
        string[] parts = givens.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
        {
            return name;
        }

        string Compose(string given) =>
            surnameFirst ? $"{surname}, {given}" : $"{given} {surname}";

        if (parts.Length > 1)
        {
            string middles = string.Join(" ", parts.Skip(1).Select(p => p[0] + "."));
            string candidate = Compose($"{parts[0]} {middles}");
            if (candidate.Length <= budget)
            {
                return candidate;
            }
            candidate = Compose(parts[0]);
            if (candidate.Length <= budget)
            {
                return candidate;
            }
        }
        return Compose(parts[0][0] + ".");
    }

    static string? YearOf(LifeEvent? life)
    {
        if (life is null)
        {
            return null;
        }
        if (life.SortYear is { } year)
        {
            return year.ToString();
        }
        var match = System.Text.RegularExpressions.Regex.Match(
            life.DateText ?? "", @"\d{4}");
        return match.Success ? match.Value : null;
    }

    static string SpanOf(LifeEvent? birth, LifeEvent? death)
    {
        string? born = YearOf(birth);
        string? died = YearOf(death);
        return (born, died) switch
        {
            ({ } b, { } d) => $"{b}–{d}",
            ({ } b, null) => "*" + b,
            (null, { } d) => "+" + d,
            _ => "",
        };
    }

    /// <summary>Real and virtual persons come through the same graph
    /// node — the box only varies the life lines.</summary>
    public void UpdateFromNode(TreePerson node)
    {
        string full = node.DisplayName is { Length: > 0 } name ? name : "(ohne Name)";
        Name = ShortenName(full, IsLarge ? 22 : 18);
        IsVirtual = node.IsVirtual;
        if (node.IsVirtual)
        {
            SpanText = "(neu)";
            BirthText = "(neu)";
            DeathText = "";
            ToolTipText = full + "\nNoch nicht in Gramps — wird beim Upload angelegt.";
            return;
        }
        SpanText = SpanOf(node.Birth, node.Death);
        BirthText = CompactLifeLine("*", node.Birth);
        DeathText = CompactLifeLine("+", node.Death);
        ToolTipText = string.Join("\n", new[]
        {
            full + (node.GrampsId is { Length: > 0 } id ? $"  [{id}]" : ""),
            LifeLine("*", node.Birth),
            LifeLine("+", node.Death),
        }.Where(line => line.Length > 0));
    }
}

/// <summary>One row in the facts/events list: a real Gramps event or a
/// pending one produced by a create-event change entry. No person-object
/// row: a church record always evidences a fact, never the person
/// itself (spec 7.3).</summary>
sealed class FactRowVM(string id) : GrampsObservable
{
    public string Id { get; } = id;   // event handle | change-entry id

    // Label = full one-line form (status texts, change-list target
    // labels); the card shows EventName/DetailLine on separate lines.
    string _label = "";
    public string Label { get => _label; set => Set(ref _label, value); }

    string _eventName = "";
    public string EventName { get => _eventName; set => Set(ref _eventName, value); }

    string _detailLine = "";
    public string DetailLine { get => _detailLine; set => Set(ref _detailLine, value); }

    string _scope = "person";
    public string Scope { get => _scope; set => Set(ref _scope, value); }

    public string? FamilyHandle { get; set; }

    int _citationCount;
    public int CitationCount { get => _citationCount; set => Set(ref _citationCount, value); }

    bool _isPendingNew;
    public bool IsPendingNew { get => _isPendingNew; set => Set(ref _isPendingNew, value); }

    /// <summary>Citations already on this row in Gramps — the link view
    /// draws its solid lines and locked checkboxes from these.</summary>
    public List<CitationRef> Citations { get; private set; } = [];

    // -- link-view state (set by GrampsViewModel.RefreshLinkView) -----

    bool _isSelected;
    public bool IsSelected { get => _isSelected; set => Set(ref _isSelected, value); }

    bool _isAssignSubject;
    public bool IsAssignSubject { get => _isAssignSubject; set => Set(ref _isAssignSubject, value); }

    bool _showCheckBox;
    public bool ShowCheckBox { get => _showCheckBox; set => Set(ref _showCheckBox, value); }

    bool _isChecked;
    public bool IsChecked { get => _isChecked; set => Set(ref _isChecked, value); }

    bool _isCheckEnabled = true;
    public bool IsCheckEnabled { get => _isCheckEnabled; set => Set(ref _isCheckEnabled, value); }

    public void UpdateFrom(PersonEvent evt)
    {
        string? shortPlace = evt.Place?.Split(',')[0].Trim();
        EventName = evt.Type + (evt.Scope == "family" ? "  [Familie]" : "");
        DetailLine = string.Join(" ", new[] { evt.DateText, shortPlace }
            .Where(part => part is { Length: > 0 }));
        Label = (EventName + " " + DetailLine).Trim();
        Scope = evt.Scope ?? "person";
        FamilyHandle = evt.FamilyHandle;
        CitationCount = evt.CitationCount;
        Citations = evt.Citations ?? [];
        IsPendingNew = false;
    }

    public void UpdateFromPending(GrampsChangeEntry entry)
    {
        bool isFamily = entry.OwnerKind is "family" or "pending-family";
        EventName = $"(neu) {entry.EventTypeLabel ?? entry.EventType}"
            + (isFamily ? "  [Familie]" : "");
        DetailLine = string.Join(" ",
            new[] { entry.EventDateText, entry.EventPlace }
                .Where(part => part is { Length: > 0 }));
        Label = (EventName + " " + DetailLine).Trim();
        Scope = isFamily ? "family" : "person";
        FamilyHandle = isFamily ? entry.OwnerHandle : null;
        CitationCount = 0;
        Citations = [];
        IsPendingNew = true;
    }
}

/// <summary>One card in the sources column: an existing citation of the
/// displayed person (key = citation handle) or an adopted finding from
/// the tray (key = "find:&lt;guid&gt;"). Target sets are fact-row ids,
/// recomputed by GrampsViewModel.RefreshLinkView.</summary>
sealed class SourceCardVM(string key) : GrampsObservable
{
    public string Key { get; } = key;

    public bool IsFinding => Key.StartsWith("find:", StringComparison.Ordinal);

    public Guid? FindingId =>
        IsFinding && Guid.TryParse(Key["find:".Length..], out var id) ? id : null;

    // Finding cards: Title = parish, Subtitle = book type + years,
    // Page = Seite (fallback scan). Existing-citation cards: Title =
    // source label, Subtitle = page. Empty lines collapse in the view.
    string _title = "";
    public string Title { get => _title; set => Set(ref _title, value); }

    string _subtitle = "";
    public string Subtitle { get => _subtitle; set => Set(ref _subtitle, value); }

    string _page = "";
    public string Page { get => _page; set => Set(ref _page, value); }

    string _toolTipText = "";
    public string ToolTipText { get => _toolTipText; set => Set(ref _toolTipText, value); }

    /// <summary>Fact rows already carrying this citation in Gramps
    /// (always empty for finding cards). Locked in assign mode — the
    /// bridge deliberately cannot detach citations (spec 2.2).</summary>
    public HashSet<string> ExistingTargets { get; } = [];

    /// <summary>Fact rows targeted by change-list entries.</summary>
    public HashSet<string> PendingTargets { get; } = [];

    bool _isSelected;
    public bool IsSelected { get => _isSelected; set => Set(ref _isSelected, value); }

    bool _isAssignSubject;
    public bool IsAssignSubject { get => _isAssignSubject; set => Set(ref _isAssignSubject, value); }

    bool _showCheckBox;
    public bool ShowCheckBox { get => _showCheckBox; set => Set(ref _showCheckBox, value); }

    bool _isChecked;
    public bool IsChecked { get => _isChecked; set => Set(ref _isChecked, value); }

    bool _isCheckEnabled = true;
    public bool IsCheckEnabled { get => _isCheckEnabled; set => Set(ref _isCheckEnabled, value); }
}

/// <summary>Families ComboBox item: the graph node plus its rendered
/// label from the center's perspective.</summary>
sealed record TreeFamilyChoice(TreeFamily Family, string Display);

/// <summary>One entry of the grouped event-type list, from the bridge's
/// /event-types catalog (= the Gramps event editor's list). Xml goes to
/// the API, Label to the user, Group to the view's grouping.</summary>
sealed record EventTypeChoice(string Group, string Xml, string Label, bool IsFamily);

enum GrampsChangeKind { AttachCitation, CreateEvent, AttachExisting, CreatePerson }

/// <summary>One entry of the change list ("Änderungsliste"): a recorded
/// user action, executed by the batch upload. Citation-bearing entries
/// REFERENCE the saved Finding — the payload is resolved from the
/// library at upload time, so a later Seite correction still reaches
/// Gramps. DependsOnId links dependent entries (event on a new person)
/// for the display tree and cascade deletion. Pending entries are
/// EDITABLE (virtual person / (neu) event), so the edited fields have
/// plain setters; the views refresh via AfterChangesMutation, not INPC
/// on the entry.</summary>
sealed class GrampsChangeEntry : GrampsObservable
{
    // settable so a CreatePerson entry can use its own id as EntityKey
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public required GrampsChangeKind Kind { get; init; }
    public required string EntityKey { get; init; }      // person/family id
    public required string EntityLabel { get; set; }
    public string? DependsOnId { get; init; }

    // The saved find this entry cites (AttachCitation always; CreateEvent
    // when a finding card was active — else the event goes citation-less).
    public Guid? FindingId { get; init; }
    public string? FindLabel { get; set; }               // display: refreshed on Seite edit

    // AttachCitation / AttachExisting
    public string? TargetKind { get; init; }             // event | pending-event
    public string? TargetHandle { get; init; }           // handle | change-entry id
    public string? TargetLabel { get; set; }             // refreshed on event edit

    // AttachExisting: which existing Gramps citation to attach
    public string? CitationHandle { get; init; }
    public string? SourceLabel { get; init; }

    // CreateEvent — EventType is the locale-independent XML name,
    // EventTypeLabel the localized display text. EventDate is the
    // EVENT's own (possibly qualified) date, not the record's. Owner
    // "pending-person"/"pending-family" references a graph node.
    public string? EventType { get; set; }
    public string? EventTypeLabel { get; set; }
    public DateSpec? EventDate { get; set; }
    public string? EventDateText { get; set; }
    public string? EventPlace { get; set; }              // by name, resolved at upload
    public string? OwnerKind { get; set; }
    public string? OwnerHandle { get; set; }
    public string? EventDescription { get; set; }

    // CreatePerson: display fields — the linkage lives in the graph,
    // keyed by this entry's id ("new:" + Id is the node id).
    public string? NewGiven { get; set; }
    public string? NewSurname { get; set; }
    public string? NewGender { get; set; }
    public string? RoleLabel { get; init; }

    string? _error;
    public string? Error
    {
        get => _error;
        set { if (Set(ref _error, value)) OnChanged(nameof(HasError)); }
    }

    public bool HasError => Error is not null;

    public string DisplayText => Kind switch
    {
        GrampsChangeKind.CreateEvent =>
            $"Neues Ereignis: {EventTypeLabel ?? EventType}"
            + (EventDateText is { Length: > 0 } ? " " + EventDateText : "")
            + (EventPlace is { Length: > 0 } ? " in " + EventPlace : "")
            + (FindingId is null ? " (ohne Zitat)" : $" — mit Zitat {FindLabel}"),
        GrampsChangeKind.AttachExisting =>
            $"Vorhandenes Zitat „{SourceLabel}“ → {TargetLabel}",
        GrampsChangeKind.CreatePerson =>
            $"Neue Person: {NewGiven} {NewSurname} ({RoleLabel ?? "?"})",
        _ => $"Zitat {FindLabel} → {TargetLabel}",
    };
}

sealed class GrampsChangeNodeVM(GrampsChangeEntry entry)
{
    public GrampsChangeEntry Entry { get; } = entry;
    public List<GrampsChangeNodeVM> Children { get; } = [];
}

sealed class GrampsChangeGroupVM
{
    public required string EntityKey { get; init; }
    public required string EntityLabel { get; init; }
    public List<GrampsChangeNodeVM> Children { get; } = [];
}
