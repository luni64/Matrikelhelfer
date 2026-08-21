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
    readonly Func<SavedEntry, SavedEntry?> _editCitation;
    readonly Action<string> _openScan;

    readonly TreeGraph _graph = new();
    TreePerson? _centerNode;
    string? _lastRealCenterId;
    string? _lastSessionId;
    bool _suppressFamilyReload;

    // editCitation = MainViewModel.EditCitation, the ONE citation-edit flow
    // (dialog + library commit + refresh) shared by the tray and the source
    // cards here - the library's owner keeps the identity/merge rules and
    // calls back OnFindingCitationChanged for the Gramps-side refresh. Its
    // return value is the entry now carrying the dialog's values (a NEW one
    // after "Als Kopie speichern"), so a copy can be adopted right away.
    // openScan = MainViewModel.NavigateTo: drives the CONNECTED browser
    // to a scan url (citation cards' Digitalisat attribute / a finding's
    // page url).
    public GrampsViewModel(GrampsBackend backend,
                           ObservableCollection<SavedEntry> library,
                           Action<string> setStatus,
                           Func<SavedEntry, SavedEntry?> editCitation,
                           Action<string> openScan)
    {
        _backend = backend;
        _library = library;
        _setStatus = setStatus;
        _editCitation = editCitation;
        _openScan = openScan;
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
        EditPersonCommand = new RelayCommand<PersonBoxVM>(b => { if (b is not null) EditPersonBox(b); });
        RemovePersonCommand = new RelayCommand<PersonBoxVM>(b => { if (b is not null) RemoveVirtualPerson(b); });
        EditEventCommand = new RelayCommand<FactRowVM>(f => { if (f is not null) EditEventRow(f); });
        RemoveEventCommand = new RelayCommand<FactRowVM>(f => { if (f is not null) RemoveEventRow(f); });
        EditCitationCommand = new RelayCommand<SourceCardVM>(c => { if (c is not null) EditCitationCard(c); });
        UnadoptCommand = new RelayCommand<SourceCardVM>(c => { if (c is not null) UnadoptFinding(c); });
        OpenCardScanCommand = new RelayCommand<SourceCardVM>(c => { if (c is not null) OpenCardScan(c); });
        ShowChangesCommand = new RelayCommand(ShowChanges, () => Changes.Count > 0);
        RefreshCommand = new RelayCommand(async void () => await RefreshCenterAsync(),
            () => _backend.IsConnected && _centerNode is { IsVirtual: false });
        Changes.CollectionChanged += (_, _) => OnChanged(nameof(ChangesSummary));
    }

    public ICommand RefreshCommand { get; }

    /// <summary>Explicit reload of the centered person from Gramps —
    /// THE remedy after a CONFLICT ("bitte neu laden") and after edits
    /// made directly in Gramps; also re-arms the staleness guard of
    /// staged corrections (see RefreshStagedExpectations).</summary>
    async Task RefreshCenterAsync()
    {
        if (_centerNode is { IsVirtual: false } center)
        {
            await LoadCenterAsync(center.Id);
            Status("Aus Gramps neu geladen.");
        }
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
                RefreshStagedExpectations(detail);
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
                var detail = await _backend.GetPersonAsync(partner.Id);
                _graph.UpsertDetail(detail);
                RefreshStagedExpectations(detail);
            }
            catch (Exception)
            {
                // brief data is good enough for the box
            }
        }
    }

    /// <summary>Re-arms the staleness guard after a fresh detail read:
    /// staged corrections targeting the objects just read take the NEW
    /// change times. The 409 CONFLICT exists to prevent BLIND overwrites
    /// - once the user has re-loaded (the reload button, clicking the
    /// center box, or any navigation) the current state is on screen and
    /// the staged corrections apply against it. Sparse sets keep
    /// outside edits to OTHER fields intact either way.</summary>
    void RefreshStagedExpectations(PersonDetail detail)
    {
        var current = new Dictionary<string, long?> { [detail.Handle] = detail.Change };
        foreach (var evt in detail.Events)
        {
            current[evt.Handle] = evt.Change;
            foreach (var reference in evt.Citations ?? [])
            {
                current[reference.Handle] = reference.Change;
            }
        }
        foreach (var reference in detail.Citations ?? [])
        {
            current[reference.Handle] = reference.Change;
        }
        foreach (var entry in Changes)
        {
            if (entry.Kind is GrampsChangeKind.EditPerson
                    or GrampsChangeKind.EditEvent
                    or GrampsChangeKind.DeleteEvent
                    or GrampsChangeKind.EditExistingCitation
                && entry.TargetHandle is { } handle
                && current.TryGetValue(handle, out var change))
            {
                entry.ExpectChange = change;
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
        ApplyStagedPersonEdits();
    }

    /// <summary>Overlays staged EditPerson corrections onto the person
    /// boxes (effective name, italic, tooltip line) so the tree never
    /// drifts from what the upload will do — a reload shows the fresh
    /// Gramps state WITH the staged correction on top, exactly like the
    /// fact rows do. Runs after every RebuildRows; UpdateFromNode has
    /// reset the boxes to the Gramps state first.</summary>
    void ApplyStagedPersonEdits()
    {
        var staged = Changes
            .Where(c => c.Kind == GrampsChangeKind.EditPerson
                        && c.TargetHandle is not null)
            .ToDictionary(c => c.TargetHandle!);
        if (staged.Count == 0)
        {
            return;
        }
        IEnumerable<PersonBoxVM?> boxes =
            [Center, Spouse, .. LeftParentsRow, .. RightParentsRow, .. ChildrenRow];
        foreach (var box in boxes)
        {
            if (box is not { IsPlaceholder: false }
                || !staged.TryGetValue(box.Id, out var entry)
                || _graph.Person(box.Id) is not { } node)
            {
                continue;
            }
            string Effective(string key, string original) =>
                entry.UpdateSet is { } set && set.TryGetValue(key, out var value)
                    ? value as string ?? "" : original;
            string given = Effective("given", node.ServerGiven);
            string surname = Effective("surname", node.ServerSurname);
            string effectiveName = surname.Length > 0 && given.Length > 0
                ? $"{surname}, {given}" : surname + given;
            box.ApplyStagedEdit(effectiveName, entry.EditSummary ?? "");
        }
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

    /// <summary>Surname for the dialog presets. Preferred source is the
    /// STRUCTURED surname the bridge delivers in the person detail
    /// (Gramps models names structured — no parsing needed). Brief-only
    /// nodes fall back to parsing the display name: "Surname, Givens"
    /// (Gramps' default format) takes the part before the comma, plain
    /// "Givens Surname" the last token (same rule as
    /// PersonBoxVM.ShortenName).</summary>
    static string SurnameOf(TreePerson person)
    {
        if (person.IsVirtual)
        {
            return person.Surname;
        }
        if (person.ServerSurname.Length > 0)
        {
            return person.ServerSurname;
        }
        string name = person.DisplayName;
        int comma = name.IndexOf(',');
        if (comma > 0)
        {
            return name[..comma].Trim();
        }
        int lastSpace = name.LastIndexOf(' ');
        return lastSpace < 0 ? "" : name[(lastSpace + 1)..];
    }

    // ---- editing/removing persons & events ----------------------------
    // Pending items are edited in place. EXISTING Gramps objects gained
    // STAGED corrections 2026-08 (persons: name/gender; events:
    // type/date/place/description + delete) - the additive-only rule was
    // deliberately softened for exactly these, because they arise WHILE
    // reading the book (a transcription typo, an imported duplicate
    // birth). Everything else (person delete, sources, places) stays
    // Gramps' own business. Staged = a change entry: reviewable,
    // removable, uploaded in the ONE transaction, guarded by the
    // object's change time (409 CONFLICT when Gramps edited it since).

    public ICommand EditPersonCommand { get; }
    public ICommand RemovePersonCommand { get; }

    void EditPersonBox(PersonBoxVM box)
    {
        if (_graph.Person(box.Id) is not { } node)
        {
            return;
        }
        if (node.IsVirtual)
        {
            EditVirtualPerson(node);
        }
        else
        {
            EditRealPerson(node);
        }
    }

    /// <summary>Stages a correction of an EXISTING person's name/gender
    /// (same dialog as creation). The person box shows the EFFECTIVE
    /// staged name in italics (ApplyStagedPersonEdits), the change list
    /// carries the summary.</summary>
    void EditRealPerson(TreePerson node)
    {
        // prefill = staged correction overlaid on the Gramps values, so
        // reopening continues the correction; the sparse set is ALWAYS a
        // diff against the ORIGINALS, so reverting un-stages
        var staged = Changes.FirstOrDefault(c =>
            c.Kind == GrampsChangeKind.EditPerson && c.TargetHandle == node.Id);
        string StagedOr(string key, string original) =>
            staged?.UpdateSet is { } stagedSet
            && stagedSet.TryGetValue(key, out var value)
                ? value as string ?? "" : original;
        var dialog = new NewPersonDialog("Person in Gramps korrigieren",
                                         StagedOr("given", node.ServerGiven),
                                         StagedOr("surname", node.ServerSurname),
                                         StagedOr("gender", node.Gender),
                                         edit: true)
        {
            Owner = System.Windows.Application.Current.MainWindow,
        };
        if (dialog.ShowDialog() != true)
        {
            return;
        }
        var set = new Dictionary<string, object?>();
        var summary = new List<string>();
        if (dialog.Given != node.ServerGiven)
        {
            set["given"] = dialog.Given;
            summary.Add($"Vorname → {dialog.Given}");
        }
        if (dialog.Surname != node.ServerSurname)
        {
            set["surname"] = dialog.Surname;
            summary.Add($"Nachname → {dialog.Surname}");
        }
        if (dialog.Gender != node.Gender)
        {
            set["gender"] = dialog.Gender;
            summary.Add($"Geschlecht → {dialog.Gender}");
        }
        if (staged is not null)
        {
            Changes.Remove(staged);   // replaced (or reverted) below
        }
        if (set.Count == 0)
        {
            if (staged is not null)
            {
                AfterChangesMutation("Korrektur aufgehoben");
            }
            else
            {
                Status("Keine Änderungen.");
            }
            return;
        }
        Changes.Add(new GrampsChangeEntry
        {
            Kind = GrampsChangeKind.EditPerson,
            EntityKey = node.Id,
            EntityLabel = node.DisplayName,
            TargetHandle = node.Id,
            TargetLabel = node.DisplayName,
            ExpectChange = node.Change,
            UpdateSet = set,
            EditSummary = string.Join(" · ", summary),
        });
        AfterChangesMutation("Personen-Korrektur vorgemerkt");
    }

    /// <summary>Edits a not-yet-uploaded virtual person (name/gender)
    /// via the NewPersonDialog in edit mode.</summary>
    void EditVirtualPerson(TreePerson node)
    {
        if (Changes.FirstOrDefault(c => c.Id == node.EntryId)
                is not { } entry)
        {
            return;
        }
        string context = "Neue Person bearbeiten"
            + (entry.RoleLabel is { Length: > 0 } role ? $" ({role})" : "");
        var dialog = new NewPersonDialog(context, node.Given, node.Surname,
                                         node.Gender, edit: true)
        {
            Owner = System.Windows.Application.Current.MainWindow,
        };
        if (dialog.ShowDialog() != true)
        {
            return;
        }
        node.Given = dialog.Given;
        node.Surname = dialog.Surname;
        if (node.Gender != dialog.Gender)
        {
            node.Gender = dialog.Gender;
            // re-place into the father/mother slot the new gender implies
            foreach (var family in node.Families)
            {
                if (family.Father == node)
                {
                    family.Father = null;
                }
                if (family.Mother == node)
                {
                    family.Mother = null;
                }
                TreeGraph.PlacePartner(family, node);
            }
        }
        entry.NewGiven = dialog.Given;
        entry.NewSurname = dialog.Surname;
        entry.NewGender = dialog.Gender;
        // dependent entries (events on / citations for this person) carry
        // the person's label too - their EntityKey is this entry's id
        string label = "Neu: " + node.DisplayName;
        foreach (var carrier in Changes.Where(c => c.EntityKey == entry.Id))
        {
            carrier.EntityLabel = label;
        }
        AfterChangesMutation($"Person {node.DisplayName} geändert");
    }

    /// <summary>Box-side twin of the change list's delete: removes the
    /// CreatePerson entry (same cascade confirmation for dependents).</summary>
    void RemoveVirtualPerson(PersonBoxVM box)
    {
        if (_graph.Person(box.Id) is { IsVirtual: true } node
            && Changes.FirstOrDefault(c => c.Id == node.EntryId) is { } entry)
        {
            DeleteEntry(entry);
        }
    }

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
        if (Changes.Any(c => c.Kind == GrampsChangeKind.DeleteEvent
                             && c.TargetHandle == fact.Id))
        {
            Status("Ereignis ist zum Löschen vorgemerkt – erst das " +
                   "Löschen aufheben.");
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

        // staged page corrections preview on the citation cards
        var citationStaged = Changes
            .Where(c => c.Kind == GrampsChangeKind.EditExistingCitation
                        && c.TargetHandle is not null)
            .ToDictionary(c => c.TargetHandle!);
        foreach (var card in SourceCards)
        {
            if (!card.IsFinding
                && citationStaged.TryGetValue(card.Key, out var stagedPage)
                && stagedPage.UpdateSet is { } stagedSet
                && stagedSet.TryGetValue("page", out var value))
            {
                card.Subtitle = $"(korrigiert) {value as string ?? "(leer)"}";
            }
        }

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
        var deleteStaged = Changes
            .Where(c => c.Kind == GrampsChangeKind.DeleteEvent
                        && c.TargetHandle is not null)
            .Select(c => c.TargetHandle!).ToHashSet();
        var editStaged = Changes
            .Where(c => c.Kind == GrampsChangeKind.EditEvent
                        && c.TargetHandle is not null)
            .ToDictionary(c => c.TargetHandle!);
        foreach (var fact in Facts)
        {
            fact.IsPendingDelete = deleteStaged.Contains(fact.Id);
            if (!fact.IsPendingNew
                && editStaged.TryGetValue(fact.Id, out var stagedEdit))
            {
                fact.ApplyStagedEdit(stagedEdit);
            }
            else
            {
                fact.IsPendingEdit = false;
            }
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
        card.Title = entry.CardTitle;
        card.Subtitle = entry.CardSubtitle;
        card.Page = entry.CardPage;
        card.ToolTipText = string.Join("\n", new[]
        {
            entry.Info.CitationTitle,
            entry.Info.PageDescription,
            entry.Comment,
        }.Where(line => line.Length > 0));
    }

    public ICommand EditCitationCommand { get; }
    public ICommand UnadoptCommand { get; }
    public ICommand OpenCardScanCommand { get; }

    void EditCitationCard(SourceCardVM card)
    {
        if (card.IsFinding)
        {
            EditFindingCitation(card);
        }
        else
        {
            EditExistingCitation(card);
        }
    }

    /// <summary>Stages a page/Fundstelle correction of an EXISTING
    /// Gramps citation ("ups, Seite war falsch") — sparse update with
    /// the change-time guard, like the person/event corrections.</summary>
    void EditExistingCitation(SourceCardVM card)
    {
        if (card.Citation is not { } reference || _centerNode is not { } center)
        {
            return;
        }
        string originalPage = reference.Page ?? "";
        // prefill with a staged correction if one exists; diff vs the
        // ORIGINAL, so typing the original back un-stages
        var staged = Changes.FirstOrDefault(c =>
            c.Kind == GrampsChangeKind.EditExistingCitation
            && c.TargetHandle == reference.Handle);
        string prefillPage = staged?.UpdateSet is { } stagedSet
            && stagedSet.TryGetValue("page", out var value)
                ? value as string ?? "" : originalPage;
        var dialog = new CitationEditDialog(
            reference.SourceTitle ?? reference.SourceLabel,
            prefillPage, "", grampsCitation: true)
        {
            Owner = System.Windows.Application.Current.MainWindow,
        };
        if (dialog.ShowDialog() != true)
        {
            return;
        }
        string page = dialog.Seite.Trim();
        if (staged is not null)
        {
            Changes.Remove(staged);   // replaced (or reverted) below
        }
        if (page == originalPage)
        {
            if (staged is not null)
            {
                AfterChangesMutation("Korrektur aufgehoben");
            }
            else
            {
                Status("Keine Änderungen.");
            }
            return;
        }
        Changes.Add(new GrampsChangeEntry
        {
            Kind = GrampsChangeKind.EditExistingCitation,
            EntityKey = center.Id,
            EntityLabel = center.DisplayName,
            TargetHandle = reference.Handle,
            TargetLabel = reference.SourceLabel,
            ExpectChange = reference.Change,
            UpdateSet = new Dictionary<string, object?>
            {
                ["page"] = page.Length == 0 ? null : page,
            },
            EditSummary =
                $"Fundstelle → {(page.Length == 0 ? "(leer)" : page)}",
        });
        AfterChangesMutation("Zitat-Korrektur vorgemerkt");
    }

    /// <summary>Drives the connected browser to the card's scan: a
    /// finding card uses its saved page url, an existing-citation card
    /// the Digitalisat attribute the app stored at capture time (hand-made
    /// Gramps citations have none).</summary>
    void OpenCardScan(SourceCardVM card)
    {
        string? url = card.IsFinding
            ? (card.FindingId is { } id
                   ? FindLibraryEntry(id)?.Info.EffectivePageUrl : null)
            : card.Citation?.Url;
        if (string.IsNullOrWhiteSpace(url))
        {
            Status("Dieses Zitat trägt keinen Scan-Link.");
            return;
        }
        _openScan(url);
    }

    /// <summary>Edits the citation-level fields of a FINDING card: Seite
    /// (a page field the scrape cannot deliver) and Kommentar (becomes
    /// the citation note in Gramps). The whole flow (dialog + library
    /// commit) runs in MainViewModel - it is the same edit the tray
    /// offers. A COPY made from here is adopted to the centered person
    /// right away: that person's different note is what the copy was
    /// made for. Book/source fields stay scrape-derived by design, and
    /// existing Gramps citation cards are read-only (no update API).</summary>
    void EditFindingCitation(SourceCardVM card)
    {
        if (card.FindingId is not { } findingId
            || FindLibraryEntry(findingId) is not { } entry)
        {
            return;
        }
        var result = _editCitation(entry);
        if (result is not null && !ReferenceEquals(result, entry)
            && _centerNode is { } center
            && !center.AdoptedFindings.Contains(result.Finding.Id))
        {
            center.AdoptedFindings.Add(result.Finding.Id);
            RefreshLinkView();
        }
    }

    /// <summary>Removes a finding card from the centered person's working
    /// set - pure staging, the find stays in the tray (unlike the tray's
    /// ✕, which deletes it). Staged assignments of this citation to the
    /// person's events would keep uploading invisibly once the card is
    /// gone, so they go with it, behind a confirmation. Citation
    /// references on pending events (CreateEvent.FindingId) stay - those
    /// events were deliberately created WITH this citation.</summary>
    void UnadoptFinding(SourceCardVM card)
    {
        if (_centerNode is not { } center || card.FindingId is not { } findingId)
        {
            return;
        }
        var factIds = Facts.Select(f => f.Id).ToHashSet();
        var staged = Changes.Where(c =>
            c.Kind == GrampsChangeKind.AttachCitation
            && c.FindingId == findingId
            && c.TargetHandle is { } target && factIds.Contains(target)).ToList();
        if (staged.Count > 0)
        {
            var answer = System.Windows.MessageBox.Show(
                $"Entfernt auch {staged.Count} vorgemerkte Zuordnung(en) dieses Zitats. Fortfahren?",
                "Fund aus Ansicht entfernen", System.Windows.MessageBoxButton.YesNo,
                System.Windows.MessageBoxImage.Question);
            if (answer != System.Windows.MessageBoxResult.Yes)
            {
                return;
            }
            foreach (var entry in staged)
            {
                Changes.Remove(entry);
            }
        }
        center.AdoptedFindings.Remove(findingId);
        if (staged.Count > 0)
        {
            AfterChangesMutation("Fund aus der Ansicht entfernt");
        }
        else
        {
            RefreshLinkView();
            Status("Fund aus der Ansicht entfernt – bleibt in der Ablage.");
        }
    }

    /// <summary>Called by MainViewModel after a citation edit (from the
    /// tray or a source card): re-derives the display labels that
    /// snapshot the page (the UPLOAD payload always resolves fresh from
    /// the library) and refreshes the views rendering the finding.</summary>
    public void OnFindingCitationChanged(Guid findingId)
    {
        if (FindLibraryEntry(findingId) is { } entry)
        {
            foreach (var carrier in Changes.Where(c => c.FindingId == findingId))
            {
                carrier.FindLabel = entry.CardPage;
            }
        }
        RebuildChangeTree();
        RefreshLinkView();
    }

    static void UpdateCitationCard(SourceCardVM card, CitationRef reference)
    {
        card.Citation = reference;
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
        // Place preset: the active finding's parish - in church-book work
        // the event place usually IS the parish village. Freely editable.
        string placePreset =
            ActiveFindingCard()?.FindingId is { } findingId
            && FindLibraryEntry(findingId)?.Info.Pfarrei is { Length: > 0 } parish
                ? parish : "";
        var dialog = new EventTypeDialog(EventTypeChoices, _lastEventType, "",
                                         place: placePreset)
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
        AddPendingEvent(choice, dialog.Description, date, dialog.DateDisplay,
                        dialog.Place);
    }

    void AddPendingEvent(EventTypeChoice eventType, string description,
                         DateSpec? eventDate, string eventDateText,
                         string place)
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
                EventPlace = NullIfEmpty(place),
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
                EventPlace = NullIfEmpty(place),
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
            EventPlace = NullIfEmpty(place),
            OwnerKind = "person",
            OwnerHandle = center.Id,
            EventDescription = NullIfEmpty(description),
        });
        AfterChangesMutation($"Neues Ereignis {eventType.Label} vorgemerkt");
    }

    public ICommand EditEventCommand { get; }
    public ICommand RemoveEventCommand { get; }

    /// <summary>Change-list label for an existing event, derived from
    /// the Gramps DTO — fact.Label may already carry the "(korrigiert)"
    /// overlay and must not leak into entry labels.</summary>
    static string EventLabelOf(PersonEvent evt)
    {
        string name = evt.Type + (evt.Scope == "family" ? "  [Familie]" : "");
        string? place = evt.Place?.Split(',')[0].Trim();
        return (name + " " + string.Join(" ", new[] { evt.DateText, place }
            .Where(part => part is { Length: > 0 }))).Trim();
    }

    void EditEventRow(FactRowVM fact)
    {
        if (fact.IsPendingNew)
        {
            EditPendingEvent(fact);
        }
        else
        {
            EditRealEvent(fact);
        }
    }

    void RemoveEventRow(FactRowVM fact)
    {
        if (fact.IsPendingNew)
        {
            RemovePendingEvent(fact);
        }
        else
        {
            ToggleDeleteRealEvent(fact);
        }
    }

    /// <summary>Stages a correction of an EXISTING event via the same
    /// dialog. SPARSE by design: only fields the user actually changed
    /// go into the update - an untouched (localized, unparseable) Gramps
    /// date is never round-tripped and clobbered. A changed date must
    /// parse (jjjj[-mm[-tt]]); emptying it clears the Gramps date.</summary>
    void EditRealEvent(FactRowVM fact)
    {
        if (fact.Source is not { } evt || EventTypeChoices.Count == 0)
        {
            return;
        }
        if (Changes.Any(c => c.Kind == GrampsChangeKind.DeleteEvent
                             && c.TargetHandle == fact.Id))
        {
            Status("Ereignis ist zum Löschen vorgemerkt.");
            return;
        }
        bool isFamily = fact.Scope == "family";
        var choices = EventTypeChoices
            .Where(c => c.IsFamily == isFamily).ToList();
        string originalDate = evt.DateText ?? "";
        string originalPlace = evt.Place ?? "";
        string originalDescription = evt.Description ?? "";

        // Prefill with the EFFECTIVE state - an already-staged correction
        // overlaid on the Gramps values - so reopening the dialog
        // continues the correction instead of showing the untouched
        // original. The staged entry stores effective values for ALL
        // fields (changed or not), "" meaning cleared.
        var staged = Changes.FirstOrDefault(c =>
            c.Kind == GrampsChangeKind.EditEvent && c.TargetHandle == fact.Id);
        var (prefillDateType, prefillDateText) = EventTypeDialog
            .SplitDateDisplay(staged?.EventDateText ?? originalDate);
        string prefillLabel = staged?.EventTypeLabel ?? evt.Type;
        var dialog = new EventTypeDialog(choices,
                                         choices.FirstOrDefault(
                                             c => c.Label == prefillLabel),
                                         prefillDateText,
                                         staged?.EventDescription
                                             ?? originalDescription,
                                         staged?.EventPlace ?? originalPlace,
                                         prefillDateType, edit: true)
        {
            Owner = System.Windows.Application.Current.MainWindow,
        };
        if (dialog.ShowDialog() != true || dialog.SelectedType is not { } choice)
        {
            return;
        }

        // The sparse set is ALWAYS a diff against the Gramps originals
        // (never against a previous staged state) - reverting a field to
        // the original drops it from the set, an empty set un-stages.
        var set = new Dictionary<string, object?>();
        var summary = new List<string>();
        if (choice.Label != evt.Type)
        {
            set["type"] = choice.Xml;
            summary.Add($"Typ → {choice.Label}");
        }
        // compare display-to-display so a qualified original ("vor 1757")
        // round-trips through the split modifier + text unchanged
        if (dialog.DateDisplay != originalDate)
        {
            if (dialog.DateText.Length == 0)
            {
                set["date"] = null;
                summary.Add("Datum → (leer)");
            }
            else
            {
                var date = ParseDate(dialog.DateText);
                if (date is null)
                {
                    Status("Datum nicht erkannt (jjjj, jjjj-mm oder " +
                           "jjjj-mm-tt) – nichts vorgemerkt.");
                    return;
                }
                date.Type = dialog.DateType;
                set["date"] = date;
                summary.Add($"Datum → {dialog.DateDisplay}");
            }
        }
        if (dialog.Place != originalPlace)
        {
            set["place"] = dialog.Place.Length == 0
                ? null : new PlaceSpec { Title = dialog.Place };
            summary.Add($"Ort → {(dialog.Place.Length == 0 ? "(leer)" : dialog.Place)}");
        }
        string description = dialog.Description.Trim();
        if (description != originalDescription)
        {
            set["description"] = description.Length == 0 ? null : description;
            summary.Add($"Beschreibung → {(description.Length == 0 ? "(leer)" : description)}");
        }

        if (staged is not null)
        {
            Changes.Remove(staged);   // replaced (or reverted) below
        }
        if (set.Count == 0)
        {
            if (staged is not null)
            {
                AfterChangesMutation("Korrektur aufgehoben");
            }
            else
            {
                Status("Keine Änderungen.");
            }
            return;
        }
        var (entityKey, entityLabel) = FactEntity(fact);
        Changes.Add(new GrampsChangeEntry
        {
            Kind = GrampsChangeKind.EditEvent,
            EntityKey = entityKey,
            EntityLabel = entityLabel,
            TargetHandle = fact.Id,
            TargetLabel = EventLabelOf(evt),
            ExpectChange = evt.Change,
            UpdateSet = set,
            EditSummary = string.Join(" · ", summary),
            // effective values for the row overlay and the next reopen
            EventTypeLabel = choice.Label,
            EventDateText = dialog.DateDisplay,
            EventPlace = dialog.Place,
            EventDescription = description,
        });
        AfterChangesMutation("Ereignis-Korrektur vorgemerkt");
    }

    /// <summary>Stages (or, on the second click, un-stages) the deletion
    /// of an EXISTING event. Staged citation assignments to it go with
    /// it (confirmed) - they would target a vanishing object. The row
    /// renders struck-through while the delete is staged.</summary>
    void ToggleDeleteRealEvent(FactRowVM fact)
    {
        if (Changes.FirstOrDefault(c =>
                c.Kind == GrampsChangeKind.DeleteEvent
                && c.TargetHandle == fact.Id) is { } staged)
        {
            Changes.Remove(staged);
            AfterChangesMutation("Löschen aufgehoben");
            return;
        }
        if (fact.Source is not { } evt)
        {
            return;
        }
        var attached = Changes.Where(c =>
            c.Kind is GrampsChangeKind.AttachCitation
                or GrampsChangeKind.AttachExisting
            && c.TargetHandle == fact.Id).ToList();
        if (attached.Count > 0)
        {
            var answer = System.Windows.MessageBox.Show(
                $"Entfernt auch {attached.Count} vorgemerkte Zuordnung(en) zu " +
                "diesem Ereignis. Fortfahren?",
                "Ereignis löschen", System.Windows.MessageBoxButton.YesNo,
                System.Windows.MessageBoxImage.Question);
            if (answer != System.Windows.MessageBoxResult.Yes)
            {
                return;
            }
            foreach (var entry in attached)
            {
                Changes.Remove(entry);
            }
        }
        // a staged edit of the same event is moot once it is deleted
        foreach (var stale in Changes.Where(c =>
                     c.Kind == GrampsChangeKind.EditEvent
                     && c.TargetHandle == fact.Id).ToList())
        {
            Changes.Remove(stale);
        }
        var (entityKey, entityLabel) = FactEntity(fact);
        Changes.Add(new GrampsChangeEntry
        {
            Kind = GrampsChangeKind.DeleteEvent,
            EntityKey = entityKey,
            EntityLabel = entityLabel,
            TargetHandle = fact.Id,
            TargetLabel = EventLabelOf(evt),
            ExpectChange = evt.Change,
            EditSummary = evt.CitationCount > 0
                ? $"trägt {evt.CitationCount} Zitat(e)" : null,
        });
        AfterChangesMutation("Löschen vorgemerkt (nochmal ✕ hebt es auf)");
    }

    /// <summary>Edits a pending "(neu)" event via the EventTypeDialog in
    /// edit mode. The type list is FILTERED to the entry's scope: a
    /// person event turning into a family event (or back) would change
    /// its owner, which assignments and the change tree hang off - such
    /// a change is delete + re-create, not an edit.</summary>
    void EditPendingEvent(FactRowVM fact)
    {
        if (Changes.FirstOrDefault(c =>
                c.Id == fact.Id && c.Kind == GrampsChangeKind.CreateEvent)
            is not { } entry)
        {
            return;
        }
        bool isFamily = entry.OwnerKind is "family" or "pending-family";
        var choices = EventTypeChoices
            .Where(c => c.IsFamily == isFamily).ToList();
        if (choices.Count == 0)
        {
            return;
        }
        var (dateType, dateText) =
            EventTypeDialog.SplitDateDisplay(entry.EventDateText ?? "");
        var dialog = new EventTypeDialog(choices,
                                         choices.FirstOrDefault(
                                             c => c.Xml == entry.EventType),
                                         dateText,
                                         entry.EventDescription ?? "",
                                         entry.EventPlace ?? "",
                                         dateType, edit: true)
        {
            Owner = System.Windows.Application.Current.MainWindow,
        };
        if (dialog.ShowDialog() != true || dialog.SelectedType is not { } choice)
        {
            return;
        }
        var date = ParseDate(dialog.DateText);
        if (date is not null)
        {
            date.Type = dialog.DateType;
        }
        entry.EventType = choice.Xml;
        entry.EventTypeLabel = choice.Label;
        entry.EventDate = date;
        entry.EventDateText = dialog.DateDisplay;
        entry.EventPlace = NullIfEmpty(dialog.Place);
        entry.EventDescription = NullIfEmpty(dialog.Description);
        RefreshPendingTargetLabels(entry);
        AfterChangesMutation($"Ereignis {choice.Label} geändert");
    }

    /// <summary>Attach entries snapshot their target's label at creation;
    /// re-derive it after an event edit so the change list stays true.</summary>
    void RefreshPendingTargetLabels(GrampsChangeEntry eventEntry)
    {
        var row = new FactRowVM(eventEntry.Id);
        row.UpdateFromPending(eventEntry);
        foreach (var attach in Changes.Where(c =>
                     c.TargetKind == "pending-event"
                     && c.TargetHandle == eventEntry.Id))
        {
            attach.TargetLabel = row.Label;
        }
    }

    /// <summary>Row-side twin of the change list's delete.</summary>
    void RemovePendingEvent(FactRowVM fact)
    {
        if (Changes.FirstOrDefault(c =>
                c.Id == fact.Id && c.Kind == GrampsChangeKind.CreateEvent)
            is { } entry)
        {
            DeleteEntry(entry);
        }
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
    public ICommand ShowChangesCommand { get; }

    /// <summary>The tab's summary row ("3 Änderungen"); the full list
    /// lives in ChangeListDialog. Notified via Changes.CollectionChanged
    /// (wired in the ctor), so add/remove/clear all update it.</summary>
    public string ChangesSummary => Changes.Count switch
    {
        0 => "Keine Änderungen",
        1 => "1 Änderung",
        var n => $"{n} Änderungen",
    };

    /// <summary>Opens the change list as its own window. The dialog
    /// shares THIS view model, so deletes and the send command inside
    /// it are the same objects, and the list stays live while open.
    /// The deletes stay in the dialog deliberately: it is the only
    /// GLOBAL view (in-place removal requires the owner to be
    /// centered), group delete has no in-place equivalent, and the
    /// blocked-upload recovery ("betroffene Änderungen bitte
    /// entfernen") happens here.</summary>
    void ShowChanges()
    {
        var dialog = new ChangeListDialog(this)
        {
            Owner = System.Windows.Application.Current.MainWindow,
        };
        dialog.ShowDialog();
    }

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

    // ---- upload (one capture-batch = ONE transaction) ----------------

    /// <summary>Confidence for every citation the upload creates. A
    /// GLOBAL setting (Einstellungen dialog, persisted in formats.json):
    /// all sources here are church books, so a per-citation grade would
    /// be repetitive - outliers are adjusted in Gramps.</summary>
    public string CitationConfidence { get; set; } = "normal";

    /// <summary>Repository/source/citation blocks for a finding,
    /// resolved from the CURRENT library state (a corrected Seite still
    /// reaches Gramps). Fixed derivations for now: title/abbreviation =
    /// CitationTitle, source key = its slug, publication = Bistum.</summary>
    static (RepositoryBlock Repo, SourceBlock Source, CitationBlock Citation,
            PersonUrlSpec? PersonUrl) MapFinding(LibraryEntry entry,
                                                 string confidence)
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
            Confidence = confidence,
            Attributes = string.IsNullOrWhiteSpace(info.EffectivePageUrl)
                ? null
                // "Digitalisat" (renamed from MH_Permalink 2026-08): a
                // neutral, self-explanatory attribute name in the Gramps
                // citation editor; the bridge reads both keys back
                : [new AttributeKV("Digitalisat", info.EffectivePageUrl)],
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

    /// <summary>Re-reads every person whose objects carry staged
    /// corrections and re-arms their expect_change (see SendAllAsync).
    /// Person ids come from the entries: EditPerson targets a person
    /// directly; event/citation corrections carry the owning person as
    /// EntityKey (a FAMILY handle for family events — any real partner's
    /// detail contains those events). A failed fetch leaves the old
    /// expectation armed, so the bridge-side guard still protects.</summary>
    async Task RefreshExpectationsBeforeSendAsync()
    {
        var personIds = new HashSet<string>();
        foreach (var entry in Changes)
        {
            switch (entry.Kind)
            {
                case GrampsChangeKind.EditPerson:
                    if (entry.TargetHandle is { } person)
                    {
                        personIds.Add(person);
                    }
                    break;
                case GrampsChangeKind.EditEvent:
                case GrampsChangeKind.DeleteEvent:
                case GrampsChangeKind.EditExistingCitation:
                    if (_graph.Family(entry.EntityKey) is { } family)
                    {
                        var partner = family.Father ?? family.Mother;
                        if (partner is { IsVirtual: false })
                        {
                            personIds.Add(partner.Id);
                        }
                    }
                    else if (_graph.Person(entry.EntityKey) is { IsVirtual: false })
                    {
                        personIds.Add(entry.EntityKey);
                    }
                    break;
            }
        }
        foreach (string id in personIds)
        {
            try
            {
                var detail = await _backend.GetPersonAsync(id);
                _graph.UpsertDetail(detail);
                RefreshStagedExpectations(detail);
            }
            catch (Exception)
            {
                // guard stays armed with the previously read value
            }
        }
        if (personIds.Count > 0)
        {
            RebuildAll();   // keep the display on the fresh read
        }
    }

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
                Place = entry.EventPlace is { } place
                    ? new PlaceSpec { Title = place } : null,
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
            var (repo, source, citation, personUrl) =
                MapFinding(libraryEntry.Entry, CitationConfidence);
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

        // staged corrections of existing objects (run last in the bridge)
        foreach (var entry in Changes.Where(c =>
                     c.Kind is GrampsChangeKind.EditPerson
                         or GrampsChangeKind.EditEvent
                         or GrampsChangeKind.EditExistingCitation))
        {
            request.Updates.Add(new BatchUpdateSpec
            {
                Type = entry.Kind switch
                {
                    GrampsChangeKind.EditPerson => "person",
                    GrampsChangeKind.EditEvent => "event",
                    _ => "citation",
                },
                Handle = entry.TargetHandle!,
                ExpectChange = entry.ExpectChange,
                Set = entry.UpdateSet!,
            });
        }
        foreach (var entry in Changes.Where(c =>
                     c.Kind == GrampsChangeKind.DeleteEvent))
        {
            request.Deletes.Add(new BatchDeleteSpec
            {
                Type = "event",
                Handle = entry.TargetHandle!,
                ExpectChange = entry.ExpectChange,
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
        // Auto-reload the affected persons right before building the
        // batch: staged corrections then apply against the CURRENT
        // Gramps state (expect_change re-armed), so the CONFLICT guard
        // only fires on a genuine race - the user never has to reload by
        // hand. Safe because the UI always shows the effective staged
        // state anyway: a Gramps-side edit to the SAME field loses to
        // the staged correction (which is what the display promised),
        // edits to other fields survive via the sparse updates.
        await RefreshExpectationsBeforeSendAsync();
        BatchRequest request;
        try
        {
            request = BuildBatch();
        }
        catch (Exception ex)
        {
            // failures that leave NOTHING saved must shout, not whisper
            // in the status bar (field feedback 2026-08)
            Status("Upload nicht möglich: " + ex.Message);
            System.Windows.MessageBox.Show(
                "Es wurde nichts nach Gramps geschrieben.\n\n" + ex.Message,
                "Upload nicht möglich", System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Error);
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

            // The uploaded findings have ARRIVED as Gramps citations -
            // drop their adoptions, or the re-read would show the same
            // find twice (the staged card next to the real citation
            // card). Findings that were adopted but never assigned stay
            // staged; a copy with its own note is a different id and
            // stays too.
            var uploadedFindings = Changes
                .Where(c => c.FindingId is not null
                            && c.Kind is GrampsChangeKind.AttachCitation
                                or GrampsChangeKind.CreateEvent)
                .Select(c => c.FindingId!.Value).ToHashSet();
            foreach (var person in _graph.AllPersons)
            {
                person.AdoptedFindings.RemoveAll(uploadedFindings.Contains);
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
            // BridgeException.Message already carries "CODE: text"
            string message = ex.Message;
            foreach (var entry in Changes)
            {
                entry.Error = "Stapel fehlgeschlagen: " + message;
            }
            Status("Upload fehlgeschlagen – nichts geschrieben: " + message);
            // a failed upload leaves NOTHING saved - that must shout,
            // not whisper in the status bar (field feedback 2026-08);
            // the change list stays complete for the retry
            System.Windows.MessageBox.Show(
                "Es wurde nichts nach Gramps geschrieben – die "
                + "Änderungsliste bleibt vollständig erhalten.\n\n" + message,
                "Upload fehlgeschlagen", System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Error);
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
