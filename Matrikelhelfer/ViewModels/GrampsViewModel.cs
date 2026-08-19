using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Input;
using GrampsBridge;
using Matrikelhelfer.Models;
using Matrikelhelfer.Services;
using Matrikelhelfer.Views;

namespace Matrikelhelfer.ViewModels;

/// <summary>
/// The Gramps-Modus view (spec 7.3), ported from the GrampsBridgeTester
/// sandbox: person search, walkable mini-tree over the local
/// person/family graph, the Ancestry-style event↔source link view and
/// the change list, uploaded as ONE capture-batch transaction.
///
/// Differences to the tester: the "current find" form is gone — finds
/// come from the shared tray (saved Findings, adopted per person), and
/// citation-bearing change entries reference the Finding by id; the
/// payload is resolved from the library at upload time.
/// </summary>
sealed class GrampsViewModel : GrampsObservable
{
    readonly GrampsBackend _backend;
    readonly ObservableCollection<SavedEntry> _library;
    readonly Action<string> _setStatus;

    readonly TreeGraph _graph = new();
    TreePerson? _centerNode;
    string? _lastRealCenterId;
    string? _lastSessionId;
    bool _suppressFamilyReload;

    public GrampsViewModel(GrampsBackend backend,
                           ObservableCollection<SavedEntry> library,
                           Action<string> setStatus)
    {
        _backend = backend;
        _library = library;
        _setStatus = setStatus;
        SearchCommand = new RelayCommand(async void () => await SearchAsync(),
            () => _backend.IsConnected && _backend.TreeOpen);
        NavigateCommand = new RelayCommand<PersonBoxVM>(box =>
        {
            if (box is not null)
            {
                BoxClicked(box);
            }
        });
        SelectFactCommand = new RelayCommand<FactRowVM>(f => { if (f is not null) FactClicked(f); });
        SelectCardCommand = new RelayCommand<SourceCardVM>(c => { if (c is not null) CardClicked(c); });
        AssignFactCommand = new RelayCommand<FactRowVM>(f => { if (f is not null) FactDoubleClicked(f); });
        AssignCardCommand = new RelayCommand<SourceCardVM>(c => { if (c is not null) CardDoubleClicked(c); });
        EndAssignCommand = new RelayCommand(EndAssign);
        AddEventCommand = new RelayCommand(OpenAddEventDialog,
            () => _centerNode is not null && EventTypeChoices.Count > 0);
        SendCommand = new RelayCommand(async void () => await SendAllAsync(),
            () => _backend.IsConnected && Changes.Count > 0);
        DeleteEntryCommand = new RelayCommand<GrampsChangeEntry>(e => { if (e is not null) DeleteEntry(e); });
        DeleteGroupCommand = new RelayCommand<GrampsChangeGroupVM>(g => { if (g is not null) DeleteGroup(g); });
    }

    void Status(string text) => _setStatus(text);

    // ---- connection hook (called by MainViewModel) -------------------

    /// <summary>After every (re)connect: load the event-type catalog and
    /// drop the graph when Gramps switched trees (FA-C4 — cached handles
    /// are invalid then; pending changes stay and fail loudly at upload
    /// if they reference the old tree).</summary>
    public async void OnConnectionChanged()
    {
        if (!_backend.IsConnected)
        {
            return;
        }
        if (_backend.SessionId != _lastSessionId)
        {
            _lastSessionId = _backend.SessionId;
            _graph.Clear();
            _centerNode = null;
            _lastRealCenterId = null;
            Center = null;
            Spouse = null;
            LeftBox = null;
            RightBox = null;
            LeftParentsRow.Clear();
            RightParentsRow.Clear();
            ChildrenRow.Clear();
            Facts.Clear();
            SourceCards.Clear();
            Families.Clear();
            SearchResults.Clear();
            OnChanged(nameof(HasMultipleFamilies));
            if (Changes.Count > 0)
            {
                Status("Gramps-Sitzung hat gewechselt – offene Änderungen " +
                       "beziehen sich ggf. auf den alten Stammbaum.");
            }
        }
        if (_backend.TreeOpen)
        {
            await LoadEventTypesAsync();
        }
    }

    // ---- search ------------------------------------------------------

    string _searchQuery = "";
    public string SearchQuery { get => _searchQuery; set => Set(ref _searchQuery, value); }

    public ObservableCollection<PersonSummary> SearchResults { get; } = [];

    PersonSummary? _selectedResult;
    public PersonSummary? SelectedResult
    {
        get => _selectedResult;
        set
        {
            if (Set(ref _selectedResult, value) && value is not null)
            {
                _ = LoadCenterAsync(value.Handle);
            }
        }
    }

    public ICommand SearchCommand { get; }

    /// <summary>"Hans 1750" -> q tokens + birth window around the year.</summary>
    async Task SearchAsync()
    {
        try
        {
            var words = new List<string>();
            int? year = null;
            foreach (string token in SearchQuery.Split(' ',
                         StringSplitOptions.RemoveEmptyEntries))
            {
                if (token.Length == 4 && int.TryParse(token, out int parsed))
                {
                    year = parsed;
                }
                else
                {
                    words.Add(token);
                }
            }
            var response = await _backend.SearchPersonsAsync(
                words.Count > 0 ? string.Join(' ', words) : null,
                year - 10, year + 10);
            SearchResults.Clear();
            foreach (var person in response.Results)
            {
                SearchResults.Add(person);
            }
            Status($"{response.Total} Treffer");
            // Center the FIRST hit right away - an empty combo next to a
            // filled result list read as "nothing found". Further hits
            // are one dropdown click away.
            if (response.Results.Count > 0)
            {
                SelectedResult = response.Results[0];
            }
        }
        catch (Exception ex)
        {
            Status("Suche fehlgeschlagen: " + ex.Message);
        }
    }

    // ---- walkable tree (over the graph) ------------------------------

    PersonBoxVM? _center;
    public PersonBoxVM? Center { get => _center; private set => Set(ref _center, value); }

    PersonBoxVM? _spouse;
    public PersonBoxVM? Spouse { get => _spouse; private set => Set(ref _spouse, value); }

    PersonBoxVM? _leftBox;
    public PersonBoxVM? LeftBox { get => _leftBox; private set => Set(ref _leftBox, value); }

    PersonBoxVM? _rightBox;
    public PersonBoxVM? RightBox { get => _rightBox; private set => Set(ref _rightBox, value); }

    public ObservableCollection<PersonBoxVM> LeftParentsRow { get; } = [];
    public ObservableCollection<PersonBoxVM> RightParentsRow { get; } = [];
    public ObservableCollection<PersonBoxVM> ChildrenRow { get; } = [];
    public ObservableCollection<FactRowVM> Facts { get; } = [];
    public ObservableCollection<TreeFamilyChoice> Families { get; } = [];

    public bool HasMultipleFamilies => Families.Count > 1;

    TreeFamilyChoice? _selectedFamily;
    public TreeFamilyChoice? SelectedFamily
    {
        get => _selectedFamily;
        set
        {
            if (Set(ref _selectedFamily, value) && !_suppressFamilyReload)
            {
                _ = OnFamilyChangedAsync();
            }
        }
    }

    public ICommand NavigateCommand { get; }

    /// <summary>Centers on any node — a Gramps person (fetch + upsert)
    /// or a virtual one (already fully present in the graph).</summary>
    async Task LoadCenterAsync(string id)
    {
        try
        {
            TreePerson? node;
            if (id.StartsWith("new:", StringComparison.Ordinal))
            {
                node = _graph.Person(id);
                if (node is null)
                {
                    return;
                }
            }
            else
            {
                if (!_backend.IsConnected)
                {
                    return;
                }
                var detail = await _backend.GetPersonAsync(id);
                node = _graph.UpsertDetail(detail);
                _lastRealCenterId = id;
            }
            _centerNode = node;
            RebuildFamilyCombo();
            await EnsurePartnerDetailAsync();
            RebuildAll();
        }
        catch (Exception ex)
        {
            Status("Person nicht ladbar: " + ex.Message);
        }
    }

    async Task EnsurePartnerDetailAsync()
    {
        var partner = _centerNode is null
            ? null : SelectedFamily?.Family.PartnerOf(_centerNode);
        if (partner is { IsVirtual: false, DetailLoaded: false }
            && _backend.IsConnected)
        {
            try
            {
                _graph.UpsertDetail(await _backend.GetPersonAsync(partner.Id));
            }
            catch (Exception)
            {
                // brief data is good enough for the box
            }
        }
    }

    async Task OnFamilyChangedAsync()
    {
        await EnsurePartnerDetailAsync();
        RebuildAll();
    }

    void RebuildFamilyCombo()
    {
        _suppressFamilyReload = true;
        var keep = SelectedFamily?.Family;
        Families.Clear();
        if (_centerNode is not null)
        {
            foreach (var family in _centerNode.Families)
            {
                Families.Add(new TreeFamilyChoice(
                    family, FamilyDisplay(_centerNode, family)));
            }
        }
        SelectedFamily = Families.FirstOrDefault(c => c.Family == keep)
                         ?? Families.FirstOrDefault();
        _suppressFamilyReload = false;
        OnChanged(nameof(HasMultipleFamilies));
    }

    static string FamilyDisplay(TreePerson center, TreeFamily family)
    {
        var partner = family.PartnerOf(center);
        return (partner is null ? "(ohne Partner)" : "mit " + partner.DisplayName)
            + $", {family.Children.Count} Kind(er)"
            + (family.IsVirtual ? " (neu)" : "");
    }

    void RebuildAll()
    {
        RebuildRows();
        SyncFacts();
        RecomputeBadges();
        RefreshLinkView();
    }

    void RebuildRows()
    {
        if (_centerNode is null)
        {
            return;
        }
        var center = _centerNode;
        var family = SelectedFamily?.Family;
        var partner = family?.PartnerOf(center);

        if (Center?.Id != center.Id)
        {
            Center = new PersonBoxVM(center.Id) { IsCenter = true, IsLarge = true };
        }
        Center.UpdateFromNode(center);

        if (partner is not null)
        {
            if (Spouse?.Id != partner.Id)
            {
                Spouse = new PersonBoxVM(partner.Id) { IsLarge = true };
            }
            Spouse.UpdateFromNode(partner);
        }
        else if (Spouse is null || !Spouse.IsPlaceholder)
        {
            Spouse = PersonBoxVM.Placeholder(large: true);
        }

        bool centerLeft = center.Gender switch
        {
            "M" => true,
            "F" => false,
            _ => (partner?.Gender ?? "U") != "M",
        };
        LeftBox = centerLeft ? Center : Spouse;
        RightBox = centerLeft ? Spouse : Center;
        SyncRow(LeftParentsRow, ParentSpecs(centerLeft ? center : partner));
        SyncRow(RightParentsRow, ParentSpecs(centerLeft ? partner : center));

        var childSpecs = new List<BoxSpec>();
        foreach (var child in family?.Children ?? [])
        {
            childSpecs.Add(NodeSpec(child));
        }
        childSpecs.Add(BoxSpec.NewSlot);
        SyncRow(ChildrenRow, childSpecs);
    }

    static List<BoxSpec> ParentSpecs(TreePerson? person)
    {
        var parentFamily = person?.ParentFamily;
        return
        [
            parentFamily?.Father is { } father ? NodeSpec(father) : BoxSpec.NewSlot,
            parentFamily?.Mother is { } mother ? NodeSpec(mother) : BoxSpec.NewSlot,
        ];
    }

    sealed record BoxSpec(string Key, Action<PersonBoxVM>? Update)
    {
        public static readonly BoxSpec NewSlot = new("", null);
    }

    static BoxSpec NodeSpec(TreePerson node) =>
        new(node.Id, box => box.UpdateFromNode(node));

    /// <summary>In-place row sync (spec 7.3: identical sets keep their
    /// boxes, so a post-upload refresh never moves the view).</summary>
    static void SyncRow(ObservableCollection<PersonBoxVM> row,
                        IReadOnlyList<BoxSpec> specs)
    {
        bool same = row.Count == specs.Count && row.Zip(specs).All(pair =>
            pair.Second.Key.Length == 0 ? pair.First.IsPlaceholder
                                        : pair.First.Id == pair.Second.Key);
        if (same)
        {
            foreach (var (box, spec) in row.Zip(specs))
            {
                spec.Update?.Invoke(box);
            }
            return;
        }
        row.Clear();
        foreach (var spec in specs)
        {
            if (spec.Key.Length == 0)
            {
                row.Add(PersonBoxVM.Placeholder());
                continue;
            }
            var box = new PersonBoxVM(spec.Key);
            spec.Update?.Invoke(box);
            row.Add(box);
        }
    }

    void SyncFacts()
    {
        var desired = new List<(string Key, Action<FactRowVM> Update)>();
        if (_centerNode is { } center)
        {
            foreach (var evt in center.Events)
            {
                desired.Add((evt.Handle, row => row.UpdateFrom(evt)));
            }
            foreach (var entry in Changes.Where(c => c.Kind == GrampsChangeKind.CreateEvent))
            {
                bool visible = entry.OwnerKind switch
                {
                    "person" => entry.OwnerHandle == center.Id,
                    "pending-person" => center.IsVirtual
                                        && entry.OwnerHandle == center.EntryId,
                    "family" or "pending-family" =>
                        entry.OwnerHandle == SelectedFamily?.Family.Id,
                    _ => false,
                };
                if (visible)
                {
                    desired.Add((entry.Id, row => row.UpdateFromPending(entry)));
                }
            }
        }

        bool same = Facts.Count == desired.Count && Facts.Zip(desired)
            .All(pair => pair.First.Id == pair.Second.Key);
        if (same)
        {
            foreach (var (row, want) in Facts.Zip(desired))
            {
                want.Update(row);
            }
            return;
        }
        Facts.Clear();
        foreach (var (key, update) in desired)
        {
            var row = new FactRowVM(key);
            update(row);
            Facts.Add(row);
        }
    }

    IEnumerable<PersonBoxVM> AllBoxes()
    {
        IEnumerable<PersonBoxVM?> boxes =
            [Center, Spouse, .. LeftParentsRow, .. RightParentsRow, .. ChildrenRow];
        return boxes.Where(b => b is { IsPlaceholder: false })!;
    }

    // ---- virtual persons (click a "Neu" box) -------------------------

    async void BoxClicked(PersonBoxVM box)
    {
        if (box.IsPlaceholder)
        {
            OpenNewPersonDialog(box);
            return;
        }
        await LoadCenterAsync(box.Id);
    }

    void OpenNewPersonDialog(PersonBoxVM box)
    {
        if (_centerNode is null)
        {
            return;
        }
        var center = _centerNode;
        var family = SelectedFamily?.Family;
        var partner = family?.PartnerOf(center);

        string context, gender, roleLabel;
        string surname = "";
        Action<TreePerson> wire;

        if (ReferenceEquals(box, Spouse))
        {
            gender = center.Gender == "M" ? "F"
                : center.Gender == "F" ? "M" : "U";
            context = "Neuer Partner von " + center.DisplayName;
            roleLabel = "Partner";
            wire = person =>
            {
                var target = family ?? _graph.AddVirtualFamily();
                TreeGraph.PlacePartner(target, center);
                TreeGraph.PlacePartner(target, person);
                if (!center.Families.Contains(target))
                {
                    center.Families.Add(target);
                }
                person.Families.Add(target);
            };
        }
        else if (LeftParentsRow.Contains(box) || RightParentsRow.Contains(box))
        {
            bool left = LeftParentsRow.Contains(box);
            var sideBox = left ? LeftBox : RightBox;
            var side = sideBox is { IsPlaceholder: false }
                ? _graph.Person(sideBox.Id) : null;
            if (side is null)
            {
                Status("Für diesen Platz zuerst die Person darunter anlegen");
                return;
            }
            gender = (left ? LeftParentsRow : RightParentsRow).IndexOf(box) == 0
                ? "M" : "F";
            surname = gender == "M" ? SurnameOf(side) : "";
            context = (gender == "M" ? "Neuer Vater von " : "Neue Mutter von ")
                + side.DisplayName;
            roleLabel = gender == "M" ? "Vater" : "Mutter";
            wire = person =>
            {
                var target = side.ParentFamily;
                if (target is null)
                {
                    target = _graph.AddVirtualFamily();
                    target.Children.Add(side);
                    side.ParentFamily = target;
                }
                TreeGraph.PlacePartner(target, person);
                person.Families.Add(target);
            };
        }
        else if (ChildrenRow.Contains(box))
        {
            gender = "U";
            var father = family?.Father
                ?? (center.Gender == "M" ? center
                    : partner?.Gender == "M" ? partner : null);
            surname = father is null ? "" : SurnameOf(father);
            context = "Neues Kind von " + center.DisplayName
                + (partner is not null ? " ⚭ " + partner.DisplayName : "");
            roleLabel = "Kind";
            wire = person =>
            {
                var target = family ?? _graph.AddVirtualFamily();
                TreeGraph.PlacePartner(target, center);
                if (!center.Families.Contains(target))
                {
                    center.Families.Add(target);
                }
                target.Children.Add(person);
                person.ParentFamily = target;
            };
        }
        else
        {
            return;
        }

        var dialog = new NewPersonDialog(context, "", surname, gender)
        {
            Owner = System.Windows.Application.Current.MainWindow,
        };
        if (dialog.ShowDialog() != true)
        {
            return;
        }
        string id = Guid.NewGuid().ToString("N");
        var node = _graph.AddVirtualPerson(id, dialog.Given, dialog.Surname,
                                           dialog.Gender);
        wire(node);
        Changes.Add(new GrampsChangeEntry
        {
            Id = id,
            Kind = GrampsChangeKind.CreatePerson,
            EntityKey = id,
            EntityLabel = "Neu: " + node.DisplayName,
            NewGiven = dialog.Given,
            NewSurname = dialog.Surname,
            NewGender = dialog.Gender,
            RoleLabel = roleLabel,
        });
        AfterChangesMutation($"Neue Person {node.DisplayName} vorgemerkt");
    }

    static string SurnameOf(TreePerson person) =>
        person.IsVirtual ? person.Surname
        : person.DisplayName.Contains(' ')
            ? person.DisplayName[(person.DisplayName.LastIndexOf(' ') + 1)..]
            : "";

    // ---- adopted findings (tray -> person) ---------------------------

    /// <summary>Tray card dragged/double-clicked onto the Gramps view:
    /// stage the finding in the CENTER person's working set. Pure UI
    /// staging — only assignments to events write anything.</summary>
    public void AdoptFinding(SavedEntry entry)
    {
        if (_centerNode is null)
        {
            Status("Zuerst eine Person suchen und auswählen.");
            return;
        }
        if (_centerNode.AdoptedFindings.Contains(entry.Finding.Id))
        {
            Status("Dieser Fund liegt bereits bei dieser Person.");
            return;
        }
        _centerNode.AdoptedFindings.Add(entry.Finding.Id);
        RefreshLinkView();
        Status($"Fund „{CardTitle(entry)}“ übernommen – per Doppelklick " +
               "Ereignisse zuordnen.");
    }

    SavedEntry? FindLibraryEntry(Guid id) =>
        _library.FirstOrDefault(e => e.Finding.Id == id);

    static string CardTitle(SavedEntry entry) => entry.Info.CitationTitle;

    // ---- event <-> source link view ----------------------------------

    public ObservableCollection<SourceCardVM> SourceCards { get; } = [];

    string? _selectedFactKey;
    string? _selectedCardKey;
    string? _assignFactKey;
    string? _assignCardKey;

    public bool InAssignMode => _assignFactKey is not null || _assignCardKey is not null;

    public string AssignHint =>
        _assignCardKey is not null
            ? "Zuordnen: Ereignisse anklicken, die dieses Zitat belegen soll"
            : _assignFactKey is not null
                ? "Zuordnen: Quellen anklicken, die dieses Ereignis belegen"
                : "";

    /// <summary>The view redraws its connector lines on this.</summary>
    public event Action? LinksChanged;

    public ICommand SelectFactCommand { get; }
    public ICommand SelectCardCommand { get; }
    public ICommand AssignFactCommand { get; }
    public ICommand AssignCardCommand { get; }
    public ICommand EndAssignCommand { get; }

    FactRowVM? FactByKey(string? key) =>
        key is null ? null : Facts.FirstOrDefault(f => f.Id == key);

    SourceCardVM? CardByKey(string? key) =>
        key is null ? null : SourceCards.FirstOrDefault(c => c.Key == key);

    void FactClicked(FactRowVM fact)
    {
        if (_assignFactKey is not null)
        {
            return;
        }
        if (CardByKey(_assignCardKey) is { } subject)
        {
            ToggleLink(fact, subject);
            return;
        }
        _selectedFactKey = fact.Id;
        _selectedCardKey = null;
        RefreshLinkView();
    }

    void CardClicked(SourceCardVM card)
    {
        if (_assignCardKey is not null)
        {
            return;
        }
        if (FactByKey(_assignFactKey) is { } subject)
        {
            ToggleLink(subject, card);
            return;
        }
        _selectedCardKey = card.Key;
        _selectedFactKey = null;
        RefreshLinkView();
    }

    void FactDoubleClicked(FactRowVM fact)
    {
        if (_assignFactKey == fact.Id)
        {
            EndAssign();
            return;
        }
        if (InAssignMode)
        {
            return;
        }
        _assignFactKey = fact.Id;
        _selectedFactKey = fact.Id;
        _selectedCardKey = null;
        RefreshLinkView();
    }

    void CardDoubleClicked(SourceCardVM card)
    {
        if (_assignCardKey == card.Key)
        {
            EndAssign();
            return;
        }
        if (InAssignMode)
        {
            return;
        }
        _assignCardKey = card.Key;
        _selectedCardKey = card.Key;
        _selectedFactKey = null;
        RefreshLinkView();
    }

    void EndAssign()
    {
        _assignFactKey = null;
        _assignCardKey = null;
        RefreshLinkView();
    }

    /// <summary>One toggle for every (fact, card) pair, whichever side
    /// anchors the assign mode. Links already existing in Gramps are
    /// locked — the bridge deliberately cannot detach citations.</summary>
    void ToggleLink(FactRowVM fact, SourceCardVM card)
    {
        if (card.ExistingTargets.Contains(fact.Id))
        {
            Status("Bereits in Gramps verknüpft – Lösen ist über die " +
                   "Bridge bewusst nicht möglich.");
            return;
        }
        var pending = card.IsFinding
            ? Changes.FirstOrDefault(c =>
                c.Kind == GrampsChangeKind.AttachCitation
                && c.FindingId == card.FindingId
                && c.TargetHandle == fact.Id)
            : Changes.FirstOrDefault(c =>
                c.Kind == GrampsChangeKind.AttachExisting
                && c.CitationHandle == card.Key
                && c.TargetHandle == fact.Id);
        if (pending is not null)
        {
            Changes.Remove(pending);
            AfterChangesMutation("Zuordnung entfernt");
            return;
        }

        string targetKind = fact.IsPendingNew ? "pending-event" : "event";
        var (entityKey, entityLabel) = FactEntity(fact);
        if (card.IsFinding)
        {
            Changes.Add(new GrampsChangeEntry
            {
                Kind = GrampsChangeKind.AttachCitation,
                FindingId = card.FindingId,
                FindLabel = card.Page,
                EntityKey = entityKey,
                EntityLabel = entityLabel,
                DependsOnId = fact.IsPendingNew ? fact.Id : null,
                TargetKind = targetKind,
                TargetHandle = fact.Id,
                TargetLabel = fact.Label,
            });
            AfterChangesMutation($"Zitat {card.Page} → {fact.Label} vorgemerkt");
        }
        else
        {
            Changes.Add(new GrampsChangeEntry
            {
                Kind = GrampsChangeKind.AttachExisting,
                CitationHandle = card.Key,
                SourceLabel = card.Title,
                EntityKey = entityKey,
                EntityLabel = entityLabel,
                DependsOnId = fact.IsPendingNew ? fact.Id : null,
                TargetKind = targetKind,
                TargetHandle = fact.Id,
                TargetLabel = fact.Label,
            });
            AfterChangesMutation($"Vorhandenes Zitat → {fact.Label} vorgemerkt");
        }
    }

    /// <summary>Rebuilds cards + link sets and re-applies all selection,
    /// assign and checkbox visuals. Single entry point after any change
    /// to facts, cards, changes or selection.</summary>
    public void RefreshLinkView()
    {
        SyncSourceCards();

        foreach (var card in SourceCards)
        {
            card.ExistingTargets.Clear();
            card.PendingTargets.Clear();
            if (card.IsFinding)
            {
                foreach (var entry in Changes)
                {
                    if (entry.Kind == GrampsChangeKind.AttachCitation
                        && entry.FindingId == card.FindingId
                        && entry.TargetHandle is { } target)
                    {
                        card.PendingTargets.Add(target);
                    }
                }
            }
            else
            {
                foreach (var fact in Facts)
                {
                    if (fact.Citations.Any(r => r.Handle == card.Key))
                    {
                        card.ExistingTargets.Add(fact.Id);
                    }
                }
                foreach (var entry in Changes)
                {
                    if (entry.Kind == GrampsChangeKind.AttachExisting
                        && entry.CitationHandle == card.Key
                        && entry.TargetHandle is { } target)
                    {
                        card.PendingTargets.Add(target);
                    }
                }
            }
        }

        if (_assignCardKey is not null && CardByKey(_assignCardKey) is null)
        {
            _assignCardKey = null;
        }
        if (_assignFactKey is not null && FactByKey(_assignFactKey) is null)
        {
            _assignFactKey = null;
        }
        if (_selectedCardKey is not null && CardByKey(_selectedCardKey) is null)
        {
            _selectedCardKey = null;
        }
        if (_selectedFactKey is not null && FactByKey(_selectedFactKey) is null)
        {
            _selectedFactKey = null;
        }

        var assignCard = CardByKey(_assignCardKey);
        foreach (var fact in Facts)
        {
            fact.IsSelected = fact.Id == _selectedFactKey;
            fact.IsAssignSubject = fact.Id == _assignFactKey;
            fact.ShowCheckBox = assignCard is not null;
            if (assignCard is not null)
            {
                bool existing = assignCard.ExistingTargets.Contains(fact.Id);
                fact.IsChecked = existing
                    || assignCard.PendingTargets.Contains(fact.Id);
                fact.IsCheckEnabled = !existing;
            }
        }
        string? assignFactKey = _assignFactKey;
        foreach (var card in SourceCards)
        {
            card.IsSelected = card.Key == _selectedCardKey;
            card.IsAssignSubject = card.Key == _assignCardKey;
            card.ShowCheckBox = assignFactKey is not null;
            if (assignFactKey is not null)
            {
                bool existing = card.ExistingTargets.Contains(assignFactKey);
                card.IsChecked = existing
                    || card.PendingTargets.Contains(assignFactKey);
                card.IsCheckEnabled = !existing;
            }
        }

        OnChanged(nameof(InAssignMode));
        OnChanged(nameof(AssignHint));
        LinksChanged?.Invoke();
    }

    /// <summary>Card list = the center's adopted findings (from the
    /// tray) + one card per distinct citation of the displayed rows,
    /// in-place synced so refreshes keep selection.</summary>
    void SyncSourceCards()
    {
        var desired = new List<(string Key, Action<SourceCardVM> Update)>();
        if (_centerNode is not null)
        {
            foreach (Guid findingId in _centerNode.AdoptedFindings.ToList())
            {
                var entry = FindLibraryEntry(findingId);
                if (entry is null)
                {
                    // finding deleted in the tray - drop the adoption
                    _centerNode.AdoptedFindings.Remove(findingId);
                    continue;
                }
                desired.Add(("find:" + findingId.ToString(),
                             card => UpdateFindingCard(card, entry)));
            }
        }
        var seen = new HashSet<string>();
        foreach (var fact in Facts)
        {
            foreach (var reference in fact.Citations)
            {
                if (seen.Add(reference.Handle))
                {
                    var captured = reference;
                    desired.Add((reference.Handle,
                                 card => UpdateCitationCard(card, captured)));
                }
            }
        }

        bool same = SourceCards.Count == desired.Count && SourceCards
            .Zip(desired).All(pair => pair.First.Key == pair.Second.Key);
        if (same)
        {
            foreach (var (card, want) in SourceCards.Zip(desired))
            {
                want.Update(card);
            }
            return;
        }
        SourceCards.Clear();
        foreach (var (key, update) in desired)
        {
            var card = new SourceCardVM(key);
            update(card);
            SourceCards.Add(card);
        }
    }

    static void UpdateFindingCard(SourceCardVM card, SavedEntry entry)
    {
        var info = entry.Info;
        card.Title = info.Pfarrei is { Length: > 0 } parish
            ? parish : info.CitationTitle;
        card.Subtitle = info.BookLabel;
        card.Page = info.Page
            ?? (info.Scan > 0 ? "Scan " + info.Scan : info.ScanLabel);
        card.ToolTipText = string.Join("\n", new[]
        {
            entry.Name,
            info.CitationTitle,
            info.PageDescription,
            entry.Comment,
        }.Where(line => line.Length > 0));
    }

    static void UpdateCitationCard(SourceCardVM card, CitationRef reference)
    {
        card.Title = reference.SourceLabel;
        card.Subtitle = reference.Page ?? "";
        card.Page = "";
        card.ToolTipText = string.Join("\n", new[]
        {
            reference.SourceTitle ?? "",
            reference.Page is { Length: > 0 } ? "Seite: " + reference.Page : "",
            reference.DateText is { Length: > 0 } ? "Datum: " + reference.DateText : "",
        }.Where(line => line.Length > 0));
    }

    /// <summary>The line pairs the view draws: everything linked to the
    /// current anchor (assign subject wins over plain selection).</summary>
    public IReadOnlyList<(FactRowVM Fact, SourceCardVM Card, bool Pending)> GetLinkPairs()
    {
        var pairs = new List<(FactRowVM, SourceCardVM, bool)>();
        var card = CardByKey(_assignCardKey ?? _selectedCardKey);
        if (card is not null)
        {
            foreach (var fact in Facts)
            {
                if (card.ExistingTargets.Contains(fact.Id))
                {
                    pairs.Add((fact, card, false));
                }
                else if (card.PendingTargets.Contains(fact.Id))
                {
                    pairs.Add((fact, card, true));
                }
            }
            return pairs;
        }
        var anchor = FactByKey(_assignFactKey ?? _selectedFactKey);
        if (anchor is not null)
        {
            foreach (var sourceCard in SourceCards)
            {
                if (sourceCard.ExistingTargets.Contains(anchor.Id))
                {
                    pairs.Add((anchor, sourceCard, false));
                }
                else if (sourceCard.PendingTargets.Contains(anchor.Id))
                {
                    pairs.Add((anchor, sourceCard, true));
                }
            }
        }
        return pairs;
    }

    // ---- new events --------------------------------------------------

    public ObservableCollection<EventTypeChoice> EventTypeChoices { get; } = [];

    EventTypeChoice? _lastEventType;

    async Task LoadEventTypesAsync()
    {
        try
        {
            var catalog = await _backend.GetEventTypesAsync();
            string keepXml = _lastEventType?.Xml ?? "Baptism";
            EventTypeChoices.Clear();
            foreach (var group in catalog.Groups)
            {
                foreach (var type in group.Types)
                {
                    EventTypeChoices.Add(new EventTypeChoice(
                        group.Name, type.Xml, type.Label, type.IsFamily));
                }
            }
            foreach (string custom in catalog.Custom)
            {
                EventTypeChoices.Add(new EventTypeChoice(
                    "Benutzerdefiniert", custom, custom, IsFamily: false));
            }
            _lastEventType =
                EventTypeChoices.FirstOrDefault(t => t.Xml == keepXml);
        }
        catch (Exception ex)
        {
            Status("Ereignistypen nicht ladbar: " + ex.Message);
        }
    }

    public ICommand AddEventCommand { get; }

    /// <summary>Which adopted finding provides the new event's citation:
    /// the assign-subject finding card, else the selected finding card,
    /// else none (the event is created citation-less).</summary>
    SourceCardVM? ActiveFindingCard()
    {
        var card = CardByKey(_assignCardKey) ?? CardByKey(_selectedCardKey);
        return card is { IsFinding: true } ? card : null;
    }

    void OpenAddEventDialog()
    {
        if (_centerNode is null || EventTypeChoices.Count == 0)
        {
            return;
        }
        var dialog = new EventTypeDialog(EventTypeChoices, _lastEventType, "")
        {
            Owner = System.Windows.Application.Current.MainWindow,
        };
        if (dialog.ShowDialog() != true || dialog.SelectedType is not { } choice)
        {
            return;
        }
        _lastEventType = choice;
        var date = ParseDate(dialog.DateText);
        if (date is not null)
        {
            date.Type = dialog.DateType;
        }
        AddPendingEvent(choice, dialog.Description, date, dialog.DateDisplay);
    }

    void AddPendingEvent(EventTypeChoice eventType, string description,
                         DateSpec? eventDate, string eventDateText)
    {
        if (_centerNode is null)
        {
            return;
        }
        var center = _centerNode;
        var findingCard = ActiveFindingCard();
        Guid? findingId = findingCard?.FindingId;
        string? findLabel = findingCard?.Page;

        if (eventType.IsFamily)
        {
            var family = SelectedFamily?.Family;
            if (family is null)
            {
                Status($"{eventType.Label} ist ein Familienereignis – " +
                       "keine Familie vorhanden.");
                return;
            }
            string entityLabel = "Familie: " + center.DisplayName
                + (family.PartnerOf(center) is { } partner
                   ? " ⚭ " + partner.DisplayName : "")
                + (family.IsVirtual ? " (neu)" : "");
            Changes.Add(new GrampsChangeEntry
            {
                Kind = GrampsChangeKind.CreateEvent,
                FindingId = findingId,
                FindLabel = findLabel,
                EntityKey = family.Id,
                EntityLabel = entityLabel,
                EventType = eventType.Xml,
                EventTypeLabel = eventType.Label,
                EventDate = eventDate,
                EventDateText = eventDateText,
                OwnerKind = family.IsVirtual ? "pending-family" : "family",
                OwnerHandle = family.Id,
                EventDescription = NullIfEmpty(description),
            });
            AfterChangesMutation($"{eventType.Label} ({entityLabel}) vorgemerkt");
            return;
        }

        if (center.IsVirtual)
        {
            if (Changes.FirstOrDefault(c => c.Id == center.EntryId)
                    is not { } personEntry)
            {
                return;
            }
            Changes.Add(new GrampsChangeEntry
            {
                Kind = GrampsChangeKind.CreateEvent,
                FindingId = findingId,
                FindLabel = findLabel,
                EntityKey = personEntry.Id,
                EntityLabel = personEntry.EntityLabel,
                DependsOnId = personEntry.Id,
                EventType = eventType.Xml,
                EventTypeLabel = eventType.Label,
                EventDate = eventDate,
                EventDateText = eventDateText,
                OwnerKind = "pending-person",
                OwnerHandle = personEntry.Id,
                EventDescription = NullIfEmpty(description),
            });
            AfterChangesMutation(
                $"{eventType.Label} für {personEntry.EntityLabel} vorgemerkt");
            return;
        }

        Changes.Add(new GrampsChangeEntry
        {
            Kind = GrampsChangeKind.CreateEvent,
            FindingId = findingId,
            FindLabel = findLabel,
            EntityKey = center.Id,
            EntityLabel = center.DisplayName,
            EventType = eventType.Xml,
            EventTypeLabel = eventType.Label,
            EventDate = eventDate,
            EventDateText = eventDateText,
            OwnerKind = "person",
            OwnerHandle = center.Id,
            EventDescription = NullIfEmpty(description),
        });
        AfterChangesMutation($"Neues Ereignis {eventType.Label} vorgemerkt");
    }

    (string Key, string Label) FactEntity(FactRowVM fact)
    {
        if (fact.IsPendingNew
            && Changes.FirstOrDefault(c => c.Id == fact.Id) is { } pending)
        {
            return (pending.EntityKey, pending.EntityLabel);
        }
        if (fact.Scope == "family" && fact.FamilyHandle is { } familyHandle)
        {
            var partner = _centerNode is not null
                ? _graph.Family(familyHandle)?.PartnerOf(_centerNode) : null;
            string label = "Familie: " + (_centerNode?.DisplayName ?? "?")
                + (partner is not null ? " ⚭ " + partner.DisplayName : "");
            return (familyHandle, label);
        }
        return (_centerNode?.Id ?? "?", _centerNode?.DisplayName ?? "?");
    }

    // ---- the change list --------------------------------------------

    public ObservableCollection<GrampsChangeEntry> Changes { get; } = [];
    public ObservableCollection<GrampsChangeGroupVM> ChangeTree { get; } = [];

    public ICommand SendCommand { get; }
    public ICommand DeleteEntryCommand { get; }
    public ICommand DeleteGroupCommand { get; }

    void DeleteEntry(GrampsChangeEntry entry)
    {
        var doomed = CollectWithDependents(entry);
        if (doomed.Count > 1)
        {
            var answer = System.Windows.MessageBox.Show(
                $"Entfernt auch {doomed.Count - 1} abhängige Änderung(en). Fortfahren?",
                "Änderung löschen", System.Windows.MessageBoxButton.YesNo,
                System.Windows.MessageBoxImage.Question);
            if (answer != System.Windows.MessageBoxResult.Yes)
            {
                return;
            }
        }
        RemoveEntriesAndNodes(doomed);
        AfterChangesMutation($"{doomed.Count} Änderung(en) entfernt");
    }

    void DeleteGroup(GrampsChangeGroupVM group)
    {
        var doomed = Changes.Where(c => c.EntityKey == group.EntityKey).ToList();
        var answer = System.Windows.MessageBox.Show(
            $"Alle {doomed.Count} Änderung(en) für \"{group.EntityLabel}\" löschen?",
            "Änderungen löschen", System.Windows.MessageBoxButton.YesNo,
            System.Windows.MessageBoxImage.Question);
        if (answer != System.Windows.MessageBoxResult.Yes)
        {
            return;
        }
        RemoveEntriesAndNodes(doomed);
        AfterChangesMutation($"{doomed.Count} Änderung(en) entfernt");
    }

    void RemoveEntriesAndNodes(List<GrampsChangeEntry> doomed)
    {
        var queue = new Queue<GrampsChangeEntry>(doomed);
        while (queue.Count > 0)
        {
            var entry = queue.Dequeue();
            Changes.Remove(entry);
            if (entry.Kind != GrampsChangeKind.CreatePerson
                || _graph.Person("new:" + entry.Id) is not { } node)
            {
                continue;
            }
            foreach (var orphan in _graph.RemoveVirtualPerson(node))
            {
                if (Changes.FirstOrDefault(c => c.Id == orphan.EntryId)
                        is { } orphanEntry)
                {
                    foreach (var dependent in CollectWithDependents(orphanEntry))
                    {
                        queue.Enqueue(dependent);
                    }
                }
            }
        }
        // family events whose (virtual) family vanished with the pruning
        foreach (var stale in Changes.Where(c =>
                     c is { Kind: GrampsChangeKind.CreateEvent,
                            OwnerKind: "pending-family" }
                     && _graph.Family(c.OwnerHandle) is null).ToList())
        {
            foreach (var dependent in CollectWithDependents(stale))
            {
                Changes.Remove(dependent);
            }
        }
        if (_centerNode is { IsVirtual: true } center
            && _graph.Person(center.Id) is null)
        {
            _centerNode = null;
            if (_lastRealCenterId is { } real)
            {
                _ = LoadCenterAsync(real);
            }
        }
    }

    List<GrampsChangeEntry> CollectWithDependents(GrampsChangeEntry root)
    {
        var result = new List<GrampsChangeEntry> { root };
        for (int i = 0; i < result.Count; i++)
        {
            result.AddRange(Changes.Where(c => c.DependsOnId == result[i].Id
                                               && !result.Contains(c)));
        }
        return result;
    }

    void AfterChangesMutation(string status)
    {
        RebuildFamilyCombo();
        RebuildRows();
        SyncFacts();
        RecomputeBadges();
        RebuildChangeTree();
        RefreshLinkView();
        Status($"{status} – {Changes.Count} Änderung(en) offen");
    }

    void RebuildChangeTree()
    {
        ChangeTree.Clear();
        foreach (var groupEntries in Changes.GroupBy(c => c.EntityKey))
        {
            var group = new GrampsChangeGroupVM
            {
                EntityKey = groupEntries.Key,
                EntityLabel = groupEntries.First().EntityLabel,
            };
            var nodes = groupEntries.ToDictionary(
                c => c.Id, c => new GrampsChangeNodeVM(c));
            foreach (var entry in groupEntries)
            {
                if (entry.DependsOnId is { } parent
                    && nodes.TryGetValue(parent, out var parentNode))
                {
                    parentNode.Children.Add(nodes[entry.Id]);
                }
                else
                {
                    group.Children.Add(nodes[entry.Id]);
                }
            }
            ChangeTree.Add(group);
        }
    }

    void RecomputeBadges()
    {
        foreach (var fact in Facts)
        {
            fact.PendingCount = Changes.Count(c =>
                c.Kind is GrampsChangeKind.AttachCitation
                    or GrampsChangeKind.AttachExisting
                && c.TargetHandle == fact.Id);
        }
        foreach (var box in AllBoxes())
        {
            box.PendingCount = box.IsVirtual
                ? Changes.Count(c =>
                    c.Kind == GrampsChangeKind.CreateEvent
                    && c.OwnerKind == "pending-person"
                    && "new:" + c.OwnerHandle == box.Id)
                : Changes.Count(c =>
                    c.Kind == GrampsChangeKind.CreateEvent
                    && c.OwnerKind == "person" && c.OwnerHandle == box.Id);
        }
    }

    // ---- upload (one capture-batch = ONE transaction) ----------------

    /// <summary>Repository/source/citation blocks for a finding,
    /// resolved from the CURRENT library state (a corrected Seite still
    /// reaches Gramps). Fixed derivations for now: title/abbreviation =
    /// CitationTitle, source key = its slug, publication = Bistum.</summary>
    static (RepositoryBlock Repo, SourceBlock Source, CitationBlock Citation,
            PersonUrlSpec? PersonUrl) MapFinding(LibraryEntry entry)
    {
        var info = entry.Page.Info;
        var (repoName, repoUrl) = RepoOf(info);
        string sourceKey = DeriveSourceKey(info.CitationTitle);
        var repo = new RepositoryBlock
        {
            Match = new MatchSpec { By = "name", Value = repoName },
            CreateIfMissing = new RepositoryCreate
            {
                Name = repoName,
                Type = repoUrl is null ? "Archive" : "Website",
                Url = repoUrl,
            },
        };
        var source = new SourceBlock
        {
            Match = new MatchSpec
            {
                By = "attribute", Key = "MH_SourceKey", Value = sourceKey,
            },
            CreateIfMissing = new SourceCreate
            {
                Title = info.CitationTitle,
                Abbreviation = info.CitationTitle,
                PublicationInfo = NullIfEmpty(info.Bistum),
                Attributes = [new AttributeKV("MH_SourceKey", sourceKey)],
                RepositoryRef = new RepoRefSpec
                {
                    CallNumber = NullIfEmpty(info.Signatur),
                    MediaType = "Book",
                },
            },
        };
        var citation = new CitationBlock
        {
            Page = NullIfEmpty(info.PageDescription),
            Confidence = "normal",
            Attributes = string.IsNullOrWhiteSpace(info.EffectivePageUrl)
                ? null
                : [new AttributeKV("MH_Permalink", info.EffectivePageUrl)],
            Notes = string.IsNullOrWhiteSpace(entry.Finding.Comment)
                ? null
                : [new NoteSpec { Type = "Citation", Text = entry.Finding.Comment }],
        };
        var personUrl = string.IsNullOrWhiteSpace(info.EffectivePageUrl)
            ? null
            : new PersonUrlSpec
            {
                Path = info.EffectivePageUrl,
                Description = "Beleg " + info.PageDescription,
                Type = "Digitalisat",
            };
        return (repo, source, citation, personUrl);
    }

    static (string Name, string? Url) RepoOf(MatriculaInfo info) =>
        info.Url.Contains("matricula-online.eu", StringComparison.OrdinalIgnoreCase)
            ? ("Matricula Online", "https://data.matricula-online.eu/")
        : info.Url.Contains("archion.de", StringComparison.OrdinalIgnoreCase)
            ? ("ARCHION", "https://www.archion.de/")
        : (NullIfEmpty(info.Bistum) ?? "Digitalisat-Archiv", null);

    /// <summary>Serializes the change list + the virtual subgraph into
    /// one batch. Throws with a user message when a referenced finding
    /// no longer exists (all-or-nothing: nothing is sent then).</summary>
    BatchRequest BuildBatch()
    {
        var request = new BatchRequest { RequestId = Guid.NewGuid().ToString() };

        foreach (var entry in Changes.Where(c => c.Kind == GrampsChangeKind.CreatePerson))
        {
            var node = _graph.Person("new:" + entry.Id)
                ?? throw new InvalidOperationException(
                    "Person fehlt im Baumgraphen: " + entry.EntityLabel);
            request.Persons.Add(new BatchPersonSpec
            {
                Tmp = node.Id,
                Given = NullIfEmpty(node.Given),
                Surname = NullIfEmpty(node.Surname),
                Gender = node.Gender,
            });
        }

        foreach (var family in _graph.AllFamilies)
        {
            if (family.IsVirtual)
            {
                request.Families.Add(new BatchFamilySpec
                {
                    Tmp = family.Id,
                    Father = family.Father?.Id,
                    Mother = family.Mother?.Id,
                    Children = family.Children.Count > 0
                        ? family.Children.Select(c => c.Id).ToList() : null,
                });
            }
            else
            {
                var spec = new BatchFamilySpec
                {
                    Handle = family.Id,
                    Father = family.Father is { IsVirtual: true } father
                        ? father.Id : null,
                    Mother = family.Mother is { IsVirtual: true } mother
                        ? mother.Id : null,
                };
                var virtualChildren = family.Children
                    .Where(c => c.IsVirtual).Select(c => c.Id).ToList();
                if (virtualChildren.Count > 0)
                {
                    spec.Children = virtualChildren;
                }
                if (spec.Father is not null || spec.Mother is not null
                    || spec.Children is not null)
                {
                    request.Families.Add(spec);
                }
            }
        }

        foreach (var entry in Changes.Where(c => c.Kind == GrampsChangeKind.CreateEvent))
        {
            bool isPerson = entry.OwnerKind is "person" or "pending-person";
            string ownerRef = entry.OwnerKind == "pending-person"
                ? "new:" + entry.OwnerHandle
                : entry.OwnerHandle!;
            request.Events.Add(new BatchEventSpec
            {
                Tmp = "evt:" + entry.Id,
                Type = entry.EventType!,
                Person = isPerson ? ownerRef : null,
                Family = isPerson ? null : ownerRef,
                Date = entry.EventDate,
                Description = entry.EventDescription,
            });
        }

        // one citation per finding, attached to everything it evidences
        foreach (var group in Changes
                     .Where(c => c.FindingId is not null
                                 && c.Kind is GrampsChangeKind.AttachCitation
                                     or GrampsChangeKind.CreateEvent)
                     .GroupBy(c => c.FindingId!.Value))
        {
            var libraryEntry = FindLibraryEntry(group.Key)
                ?? throw new InvalidOperationException(
                    "Ein zugeordneter Fund wurde inzwischen gelöscht – " +
                    "betroffene Änderungen bitte entfernen.");
            var (repo, source, citation, personUrl) = MapFinding(libraryEntry.Entry);
            var targets = new List<BatchTargetRef>();
            var seen = new HashSet<(string, string)>();
            foreach (var entry in group)
            {
                var (type, reference) = entry.Kind == GrampsChangeKind.CreateEvent
                    ? ("event", "evt:" + entry.Id)
                    : entry.TargetKind == "pending-event"
                        ? ("event", "evt:" + entry.TargetHandle)
                        : (entry.TargetKind!, entry.TargetHandle!);
                if (seen.Add((type, reference)))
                {
                    targets.Add(new BatchTargetRef { Type = type, Ref = reference });
                }
            }
            request.Citations.Add(new BatchCitationSpec
            {
                Repository = repo,
                Source = source,
                Citation = citation,
                Targets = targets,
                PersonUrl = personUrl,
            });
        }

        foreach (var group in Changes
                     .Where(c => c.Kind == GrampsChangeKind.AttachExisting)
                     .GroupBy(c => c.CitationHandle!))
        {
            request.Attach.Add(new BatchAttachSpec
            {
                Citation = group.Key,
                Targets = group.Select(entry => new BatchTargetRef
                {
                    Type = entry.TargetKind == "pending-event"
                        ? "event" : entry.TargetKind!,
                    Ref = entry.TargetKind == "pending-event"
                        ? "evt:" + entry.TargetHandle : entry.TargetHandle!,
                }).ToList(),
            });
        }
        return request;
    }

    async Task SendAllAsync()
    {
        if (!_backend.IsConnected || Changes.Count == 0)
        {
            return;
        }
        BatchRequest request;
        try
        {
            request = BuildBatch();
        }
        catch (Exception ex)
        {
            Status("Upload nicht möglich: " + ex.Message);
            return;
        }
        try
        {
            var response = await _backend.CaptureBatchAsync(request);

            // temp ids -> real handles, node identity kept (no view jump)
            foreach (var (tmp, created) in response.Created.Persons)
            {
                if (_graph.Person(tmp) is { } node)
                {
                    _graph.ReplacePersonId(node, created.Handle);
                }
            }
            foreach (var (tmp, created) in response.Created.Families)
            {
                if (_graph.Family(tmp) is { } family)
                {
                    _graph.ReplaceFamilyId(family, created.Handle);
                }
            }

            int count = Changes.Count;
            Changes.Clear();
            if (_centerNode is not null)
            {
                await LoadCenterAsync(_centerNode.Id);
            }
            AfterChangesMutation(
                $"{count} Änderung(en) in EINER Transaktion nach Gramps " +
                "geschrieben (ein Undo in Gramps)");
        }
        catch (Exception ex)
        {
            string message = ex is BridgeException bridge
                ? $"{bridge.Code}: {bridge.Message}" : ex.Message;
            foreach (var entry in Changes)
            {
                entry.Error = "Stapel fehlgeschlagen: " + message;
            }
            Status("Upload fehlgeschlagen – nichts geschrieben: " + message);
        }
    }

    // ---- helpers -----------------------------------------------------

    static string DeriveSourceKey(string title)
    {
        string text = title.ToLowerInvariant()
            .Replace("ä", "ae").Replace("ö", "oe").Replace("ü", "ue")
            .Replace("ß", "ss");
        text = new string(text.Normalize(System.Text.NormalizationForm.FormD)
            .Where(c => System.Globalization.CharUnicodeInfo.GetUnicodeCategory(c)
                        != System.Globalization.UnicodeCategory.NonSpacingMark)
            .ToArray());
        var builder = new System.Text.StringBuilder(text.Length);
        bool pendingDash = false;
        foreach (char c in text)
        {
            if (char.IsAsciiLetterOrDigit(c))
            {
                if (pendingDash && builder.Length > 0)
                {
                    builder.Append('-');
                }
                pendingDash = false;
                builder.Append(c);
            }
            else
            {
                pendingDash = true;
            }
        }
        return builder.Length > 0 ? builder.ToString() : "empty";
    }

    static string? NullIfEmpty(string value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    static DateSpec? ParseDate(string value)
    {
        string[] parts = value.Trim().Split('-');
        if (parts.Length == 0 || !int.TryParse(parts[0], out int year))
        {
            return null;
        }
        var spec = new DateSpec { Type = "regular", Year = year };
        if (parts.Length > 1 && int.TryParse(parts[1], out int month))
        {
            spec.Month = month;
        }
        if (parts.Length > 2 && int.TryParse(parts[2], out int day))
        {
            spec.Day = day;
        }
        return spec;
    }
}
