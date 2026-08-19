using System.Collections.ObjectModel;
using System.Text.Json;
using System.Windows.Input;
using GrampsBridge;

namespace GrampsBridgeTester;

/// <summary>
/// Prototype of the "Gramps-Modus" view (spec 7.3): search, walkable
/// mini-tree over a local person/family graph (TreeGraph — loaded and
/// newly created persons are the same kind of node), an Ancestry-style
/// event↔source link view (click draws connector lines, double-click
/// enters assign mode) and a change list ("Änderungsliste") — every
/// user action is recorded as a deletable entry; upload executes the
/// list with dependency ordering and shared-citation coalescing, then
/// reads the displayed slice back in place.
/// </summary>
public sealed class MainViewModel : ObservableObject
{
    private BridgeClient? _client;
    private bool _suppressFamilyReload;

    private readonly TreeGraph _graph = new();
    private TreePerson? _centerNode;
    private string? _lastRealCenterId;   // fallback when a virtual center is deleted

    public MainViewModel()
    {
        DiscoveryPath = Discovery.DefaultPath;
        ConnectCommand = new RelayCommand(async void () => await ConnectAsync());
        SearchCommand = new RelayCommand(async void () => await SearchAsync(),
                                         () => _client is not null);
        SendCommand = new RelayCommand(async void () => await SendAllAsync(),
            () => _client is not null && Changes.Count > 0);
        AddEventCommand = new RelayCommand(OpenAddEventDialog,
            () => _centerNode is not null && EventTypeChoices.Count > 0);
        NavigateCommand = new RelayCommand<PersonBoxVM>(BoxClicked);
        SelectFactCommand = new RelayCommand<FactRowVM>(FactClicked);
        SelectCardCommand = new RelayCommand<SourceCardVM>(CardClicked);
        AssignFactCommand = new RelayCommand<FactRowVM>(FactDoubleClicked);
        AssignCardCommand = new RelayCommand<SourceCardVM>(CardDoubleClicked);
        EndAssignCommand = new RelayCommand(EndAssign);
        DeleteEntryCommand = new RelayCommand<ChangeEntry>(DeleteEntry);
        DeleteGroupCommand = new RelayCommand<ChangeGroupVM>(DeleteGroup);
        SourceKey = DeriveSourceKey(SourceTitle);
        Abbrev = DeriveAbbrevText(SourceTitle);
        Permalink = ComputePermalink();
    }

    // ---- connection --------------------------------------------------

    private string _discoveryPath = "";
    public string DiscoveryPath { get => _discoveryPath; set => Set(ref _discoveryPath, value); }

    private string _connectionStatus = "not connected";
    public string ConnectionStatus { get => _connectionStatus; set => Set(ref _connectionStatus, value); }

    public ICommand ConnectCommand { get; }

    private async Task ConnectAsync()
    {
        try
        {
            var endpoint = Discovery.Load(
                string.IsNullOrWhiteSpace(DiscoveryPath) ? null : DiscoveryPath);
            if (endpoint is null)
            {
                ConnectionStatus = "discovery file not found — is Gramps running?";
                _client = null;
                return;
            }
            if (!endpoint.IsProcessAlive())
            {
                ConnectionStatus = $"stale discovery file (pid {endpoint.Pid} dead)";
                _client = null;
                return;
            }
            var client = new BridgeClient(endpoint);
            var ping = await client.PingAsync();
            _client = client;
            ConnectionStatus =
                $"connected: Gramps {ping.GrampsVersion}, addon {ping.AddonVersion}, "
                + (ping.TreeOpen ? $"tree \"{ping.TreeName}\"" : "NO TREE OPEN");
            if (ping.TreeOpen)
                await LoadEventTypesAsync();
        }
        catch (Exception ex)
        {
            _client = null;
            ConnectionStatus = "connect failed: " + ex.Message;
        }
    }

    // ---- search ------------------------------------------------------

    private string _searchQuery = "";
    public string SearchQuery { get => _searchQuery; set => Set(ref _searchQuery, value); }

    private string _searchStatus = "";
    public string SearchStatus { get => _searchStatus; set => Set(ref _searchStatus, value); }

    public ObservableCollection<PersonSummary> SearchResults { get; } = [];

    private PersonSummary? _selectedResult;
    public PersonSummary? SelectedResult
    {
        get => _selectedResult;
        set
        {
            if (Set(ref _selectedResult, value) && value is not null)
                _ = LoadCenterAsync(value.Handle);
        }
    }

    public ICommand SearchCommand { get; }

    /// <summary>"Hans 1750" -> q tokens + birth window around the year.</summary>
    private async Task SearchAsync()
    {
        if (_client is null)
            return;
        try
        {
            var words = new List<string>();
            int? year = null;
            foreach (var token in SearchQuery.Split(' ',
                         StringSplitOptions.RemoveEmptyEntries))
            {
                if (token.Length == 4 && int.TryParse(token, out var parsed))
                    year = parsed;
                else
                    words.Add(token);
            }
            var response = await _client.SearchPersonsAsync(
                q: words.Count > 0 ? string.Join(' ', words) : null,
                birthYearFrom: year - 10, birthYearTo: year + 10);
            SearchResults.Clear();
            foreach (var person in response.Results)
                SearchResults.Add(person);
            SearchStatus = $"{response.Total} Treffer";
            if (response.Results.Count == 1)
                SelectedResult = response.Results[0];
        }
        catch (Exception ex)
        {
            SearchStatus = "search failed: " + ex.Message;
        }
    }

    // ---- walkable tree (over the graph) ------------------------------

    private PersonBoxVM? _center;
    public PersonBoxVM? Center { get => _center; set => Set(ref _center, value); }

    private PersonBoxVM? _spouse;
    public PersonBoxVM? Spouse { get => _spouse; set => Set(ref _spouse, value); }

    private PersonBoxVM? _leftBox;
    public PersonBoxVM? LeftBox { get => _leftBox; set => Set(ref _leftBox, value); }

    private PersonBoxVM? _rightBox;
    public PersonBoxVM? RightBox { get => _rightBox; set => Set(ref _rightBox, value); }

    public ObservableCollection<PersonBoxVM> LeftParentsRow { get; } = [];
    public ObservableCollection<PersonBoxVM> RightParentsRow { get; } = [];
    public ObservableCollection<PersonBoxVM> ChildrenRow { get; } = [];
    public ObservableCollection<FactRowVM> Facts { get; } = [];
    public ObservableCollection<TreeFamilyChoice> Families { get; } = [];

    public bool HasMultipleFamilies => Families.Count > 1;

    private TreeFamilyChoice? _selectedFamily;
    public TreeFamilyChoice? SelectedFamily
    {
        get => _selectedFamily;
        set
        {
            if (Set(ref _selectedFamily, value) && !_suppressFamilyReload)
                _ = OnFamilyChangedAsync();
        }
    }

    public ICommand NavigateCommand { get; }

    /// <summary>Centers on any node — a Gramps person (fetch + upsert)
    /// or a virtual one (already fully present in the graph). Same
    /// gesture, same code path.</summary>
    private async Task LoadCenterAsync(string id)
    {
        try
        {
            TreePerson? node;
            if (id.StartsWith("new:", StringComparison.Ordinal))
            {
                node = _graph.Person(id);
                if (node is null)
                    return;
            }
            else
            {
                if (_client is null)
                    return;
                var detail = await _client.GetPersonAsync(id);
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
            SearchStatus = "load failed: " + ex.Message;
        }
    }

    /// <summary>The displayed partner's parents live one detail call
    /// away — fetch once, the graph keeps it.</summary>
    private async Task EnsurePartnerDetailAsync()
    {
        var partner = _centerNode is null
            ? null : SelectedFamily?.Family.PartnerOf(_centerNode);
        if (partner is { IsVirtual: false, DetailLoaded: false }
            && _client is not null)
        {
            try
            {
                _graph.UpsertDetail(await _client.GetPersonAsync(partner.Id));
            }
            catch (Exception)
            {
                // brief data is good enough for the box
            }
        }
    }

    private async Task OnFamilyChangedAsync()
    {
        await EnsurePartnerDetailAsync();
        RebuildAll();
    }

    private void RebuildFamilyCombo()
    {
        _suppressFamilyReload = true;
        var keep = SelectedFamily?.Family;
        Families.Clear();
        if (_centerNode is not null)
            foreach (var family in _centerNode.Families)
                Families.Add(new TreeFamilyChoice(
                    family, FamilyDisplay(_centerNode, family)));
        SelectedFamily = Families.FirstOrDefault(c => c.Family == keep)
                         ?? Families.FirstOrDefault();
        _suppressFamilyReload = false;
        OnChanged(nameof(HasMultipleFamilies));
    }

    private static string FamilyDisplay(TreePerson center, TreeFamily family)
    {
        var partner = family.PartnerOf(center);
        return (partner is null ? "(ohne Partner)" : "mit " + partner.DisplayName)
            + $", {family.Children.Count} Kind(er)"
            + (family.IsVirtual ? " (neu)" : "");
    }

    private void RebuildAll()
    {
        RebuildRows();
        SyncFacts();
        RecomputeBadges();
        RefreshLinkView();
    }

    /// <summary>Layout straight from the graph: couple (man left, woman
    /// right), each side's parent family above, the selected family's
    /// children below. Virtual nodes render like real ones.</summary>
    private void RebuildRows()
    {
        if (_centerNode is null)
            return;
        var center = _centerNode;
        var family = SelectedFamily?.Family;
        var partner = family?.PartnerOf(center);

        if (Center?.Handle != center.Id)
            Center = new PersonBoxVM(center.Id) { IsCenter = true, IsLarge = true };
        Center.UpdateFromNode(center);

        if (partner is not null)
        {
            if (Spouse?.Handle != partner.Id)
                Spouse = new PersonBoxVM(partner.Id) { IsLarge = true };
            Spouse.UpdateFromNode(partner);
        }
        else if (Spouse is null || !Spouse.IsPlaceholder)
        {
            Spouse = PersonBoxVM.Placeholder(large: true);
        }

        var centerLeft = center.Gender switch
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
            childSpecs.Add(NodeSpec(child));
        childSpecs.Add(BoxSpec.NewSlot);
        SyncRow(ChildrenRow, childSpecs);
    }

    /// <summary>Two slots above each partner: father left, mother right,
    /// empty slot = "Neu".</summary>
    private static List<BoxSpec> ParentSpecs(TreePerson? person)
    {
        var parentFamily = person?.ParentFamily;
        return
        [
            parentFamily?.Father is { } father ? NodeSpec(father) : BoxSpec.NewSlot,
            parentFamily?.Mother is { } mother ? NodeSpec(mother) : BoxSpec.NewSlot,
        ];
    }

    private sealed record BoxSpec(string Key, Action<PersonBoxVM>? Update)
    {
        public static readonly BoxSpec NewSlot = new("", null);
    }

    private static BoxSpec NodeSpec(TreePerson node) =>
        new(node.Id, box => box.UpdateFromNode(node));

    /// <summary>In-place row sync (spec 7.3: identical sets keep their
    /// boxes, so a post-upload refresh never moves the view). An empty
    /// key means a "Neu" placeholder slot.</summary>
    private static void SyncRow(ObservableCollection<PersonBoxVM> row,
                                IReadOnlyList<BoxSpec> specs)
    {
        var same = row.Count == specs.Count && row.Zip(specs).All(pair =>
            pair.Second.Key.Length == 0 ? pair.First.IsPlaceholder
                                        : pair.First.Handle == pair.Second.Key);
        if (same)
        {
            foreach (var (box, spec) in row.Zip(specs))
                spec.Update?.Invoke(box);
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

    /// <summary>Facts of the center: its Gramps events plus pending
    /// create-event entries; a virtual center has pending events only.
    /// Pending rows are keyed by their change-entry id, so they turn
    /// into the real row in place after the upload read-back.</summary>
    private void SyncFacts()
    {
        var desired = new List<(string Key, Action<FactRowVM> Update)>();
        if (_centerNode is { } center)
        {
            foreach (var evt in center.Events)
                desired.Add((evt.Handle, row => row.UpdateFrom(evt)));
            foreach (var entry in Changes.Where(c => c.Kind == ChangeKind.CreateEvent))
            {
                var visible = entry.OwnerKind switch
                {
                    "person" => entry.OwnerHandle == center.Id,
                    "pending-person" => center.IsVirtual
                                        && entry.OwnerHandle == center.EntryId,
                    "family" or "pending-family" =>
                        entry.OwnerHandle == SelectedFamily?.Family.Id,
                    _ => false,
                };
                if (visible)
                    desired.Add((entry.Id, row => row.UpdateFromPending(entry)));
            }
        }

        var same = Facts.Count == desired.Count && Facts.Zip(desired)
            .All(pair => pair.First.Handle == pair.Second.Key);
        if (same)
        {
            foreach (var (row, want) in Facts.Zip(desired))
                want.Update(row);
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

    private IEnumerable<PersonBoxVM> AllBoxes()
    {
        IEnumerable<PersonBoxVM?> boxes =
            [Center, Spouse, .. LeftParentsRow, .. RightParentsRow, .. ChildrenRow];
        return boxes.Where(b => b is { IsPlaceholder: false })!;
    }

    // ---- virtual persons (spec 7.3 v2: click a "Neu" box) ------------

    /// <summary>Any person box navigates — real ones fetch, virtual
    /// ones are already in the graph. "Neu" slots create a person.</summary>
    private async void BoxClicked(PersonBoxVM box)
    {
        if (box.IsPlaceholder)
        {
            OpenNewPersonDialog(box);
            return;
        }
        await LoadCenterAsync(box.Handle);
    }

    private void OpenNewPersonDialog(PersonBoxVM box)
    {
        if (_centerNode is null)
            return;
        var center = _centerNode;
        var family = SelectedFamily?.Family;
        var partner = family?.PartnerOf(center);

        string context, gender, roleLabel;
        var surname = "";
        Action<TreePerson> wire;

        if (ReferenceEquals(box, Spouse))
        {
            gender = center.Gender == "M" ? "F"
                : center.Gender == "F" ? "M" : "U";
            context = "Neuer Partner von " + center.DisplayName;
            roleLabel = "Partner";
            wire = person =>
            {
                // join the partner-less selected family, else found one
                var target = family ?? _graph.AddVirtualFamily();
                TreeGraph.PlacePartner(target, center);
                TreeGraph.PlacePartner(target, person);
                if (!center.Families.Contains(target))
                    center.Families.Add(target);
                person.Families.Add(target);
            };
        }
        else if (LeftParentsRow.Contains(box) || RightParentsRow.Contains(box))
        {
            var left = LeftParentsRow.Contains(box);
            var sideBox = left ? LeftBox : RightBox;
            var side = sideBox is { IsPlaceholder: false }
                ? _graph.Person(sideBox.Handle) : null;
            if (side is null)
            {
                QueueStatus = "Für diesen Platz zuerst die Person darunter anlegen";
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
                    center.Families.Add(target);
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
            return;
        var id = Guid.NewGuid().ToString("N");
        var node = _graph.AddVirtualPerson(id, dialog.Given, dialog.Surname,
                                           dialog.Gender);
        wire(node);
        Changes.Add(new ChangeEntry
        {
            Id = id,
            Kind = ChangeKind.CreatePerson,
            EntityKey = id,      // the new person is its own change-tree root
            EntityLabel = "Neu: " + node.DisplayName,
            NewGiven = dialog.Given,
            NewSurname = dialog.Surname,
            NewGender = dialog.Gender,
            RoleLabel = roleLabel,
        });
        AfterChangesMutation(
            $"Änderung erfasst: neue Person {node.DisplayName}");
    }

    private static string SurnameOf(TreePerson person) =>
        person.IsVirtual ? person.Surname
        : person.DisplayName.Contains(' ')
            ? person.DisplayName[(person.DisplayName.LastIndexOf(' ') + 1)..]
            : "";

    // ---- event <-> source link view (Ancestry-style) -----------------
    //
    // Selection anchors are stored as KEYS, not object references: the
    // in-place syncs may rebuild rows/cards, and a key survives that.

    public ObservableCollection<SourceCardVM> SourceCards { get; } = [];

    private string? _selectedFactKey;
    private string? _selectedCardKey;
    private string? _assignFactKey;    // assign mode anchored at a fact
    private string? _assignCardKey;    // assign mode anchored at a card

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

    private FactRowVM? FactByKey(string? key) =>
        key is null ? null : Facts.FirstOrDefault(f => f.Handle == key);

    private SourceCardVM? CardByKey(string? key) =>
        key is null ? null : SourceCards.FirstOrDefault(c => c.Key == key);

    /// <summary>Single click: in assign mode a fact click toggles the
    /// link to the assign card; otherwise it selects (draws lines).</summary>
    private void FactClicked(FactRowVM fact)
    {
        if (_assignFactKey is not null)
            return;                                  // facts are the subject
        if (CardByKey(_assignCardKey) is { } subject)
        {
            ToggleLink(fact, subject);
            return;
        }
        _selectedFactKey = fact.Handle;
        _selectedCardKey = null;
        RefreshLinkView();
    }

    private void CardClicked(SourceCardVM card)
    {
        if (_assignCardKey is not null)
            return;                                  // cards are the subject
        if (FactByKey(_assignFactKey) is { } subject)
        {
            ToggleLink(subject, card);
            return;
        }
        _selectedCardKey = card.Key;
        _selectedFactKey = null;
        RefreshLinkView();
    }

    /// <summary>Double click enters assign mode with this row as the
    /// subject; double-clicking the subject again leaves it.</summary>
    private void FactDoubleClicked(FactRowVM fact)
    {
        if (_assignFactKey == fact.Handle)
        {
            EndAssign();
            return;
        }
        if (InAssignMode)
            return;
        _assignFactKey = fact.Handle;
        _selectedFactKey = fact.Handle;
        _selectedCardKey = null;
        RefreshLinkView();
    }

    private void CardDoubleClicked(SourceCardVM card)
    {
        if (_assignCardKey == card.Key)
        {
            EndAssign();
            return;
        }
        if (InAssignMode)
            return;
        _assignCardKey = card.Key;
        _selectedCardKey = card.Key;
        _selectedFactKey = null;
        RefreshLinkView();
    }

    private void EndAssign()
    {
        _assignFactKey = null;
        _assignCardKey = null;
        RefreshLinkView();
    }

    /// <summary>One toggle for every (fact, card) pair, whichever side
    /// anchors the assign mode. Links already existing in Gramps are
    /// locked — the bridge deliberately cannot detach citations.</summary>
    private void ToggleLink(FactRowVM fact, SourceCardVM card)
    {
        if (card.ExistingTargets.Contains(fact.Handle))
        {
            QueueStatus = "bereits in Gramps verknüpft — Lösen ist über "
                          + "die Bridge bewusst nicht möglich";
            return;
        }
        var pending = card.IsFind
            ? Changes.FirstOrDefault(c =>
                c.Kind == ChangeKind.AttachCitation
                && c.Find!.GroupKey == SnapshotFind().GroupKey
                && c.TargetHandle == fact.Handle)
            : Changes.FirstOrDefault(c =>
                c.Kind == ChangeKind.AttachExisting
                && c.CitationHandle == card.Key
                && c.TargetHandle == fact.Handle);
        if (pending is not null)
        {
            Changes.Remove(pending);
            AfterChangesMutation("Zuordnung entfernt");
            return;
        }

        // no person-object targets: a church record always evidences a
        // fact, never the person itself (spec 7.3, 2026-08-18)
        var targetKind = fact.IsPendingNew ? "pending-event" : "event";
        var (entityKey, entityLabel) = FactEntity(fact);
        if (card.IsFind)
        {
            var find = SnapshotFind();
            Changes.Add(new ChangeEntry
            {
                Kind = ChangeKind.AttachCitation,
                Find = find,
                EntityKey = entityKey,
                EntityLabel = entityLabel,
                DependsOnId = fact.IsPendingNew ? fact.Handle : null,
                TargetKind = targetKind,
                TargetHandle = fact.Handle,
                TargetLabel = fact.Label,
            });
            AfterChangesMutation(
                $"Änderung erfasst: Zitat {find.Page} → {fact.Label}");
        }
        else
        {
            Changes.Add(new ChangeEntry
            {
                Kind = ChangeKind.AttachExisting,
                CitationHandle = card.Key,
                SourceLabel = card.Title,
                EntityKey = entityKey,
                EntityLabel = entityLabel,
                DependsOnId = fact.IsPendingNew ? fact.Handle : null,
                TargetKind = targetKind,
                TargetHandle = fact.Handle,
                TargetLabel = fact.Label,
            });
            AfterChangesMutation(
                $"Änderung erfasst: vorhandenes Zitat → {fact.Label}");
        }
    }

    /// <summary>Rebuilds cards + link sets and re-applies all selection,
    /// assign and checkbox visuals. Single entry point after any change
    /// to facts, cards, changes or selection.</summary>
    private void RefreshLinkView()
    {
        SyncSourceCards();

        var groupKey = SnapshotFind().GroupKey;
        foreach (var card in SourceCards)
        {
            card.ExistingTargets.Clear();
            card.PendingTargets.Clear();
            if (card.IsFind)
            {
                foreach (var entry in Changes)
                    if (entry.Kind == ChangeKind.AttachCitation
                        && entry.Find!.GroupKey == groupKey
                        && entry.TargetHandle is { } target)
                        card.PendingTargets.Add(target);
            }
            else
            {
                foreach (var fact in Facts)
                    if (fact.Citations.Any(r => r.Handle == card.Key))
                        card.ExistingTargets.Add(fact.Handle);
                foreach (var entry in Changes)
                    if (entry.Kind == ChangeKind.AttachExisting
                        && entry.CitationHandle == card.Key
                        && entry.TargetHandle is { } target)
                        card.PendingTargets.Add(target);
            }
        }

        // anchors whose row/card vanished (navigation, deletion)
        if (_assignCardKey is not null && CardByKey(_assignCardKey) is null)
            _assignCardKey = null;
        if (_assignFactKey is not null && FactByKey(_assignFactKey) is null)
            _assignFactKey = null;
        if (_selectedCardKey is not null && CardByKey(_selectedCardKey) is null)
            _selectedCardKey = null;
        if (_selectedFactKey is not null && FactByKey(_selectedFactKey) is null)
            _selectedFactKey = null;

        var assignCard = CardByKey(_assignCardKey);
        foreach (var fact in Facts)
        {
            fact.IsSelected = fact.Handle == _selectedFactKey;
            fact.IsAssignSubject = fact.Handle == _assignFactKey;
            fact.ShowCheckBox = assignCard is not null;
            if (assignCard is not null)
            {
                var existing = assignCard.ExistingTargets.Contains(fact.Handle);
                fact.IsChecked = existing
                    || assignCard.PendingTargets.Contains(fact.Handle);
                fact.IsCheckEnabled = !existing;
            }
        }
        var assignFactKey = _assignFactKey;
        foreach (var card in SourceCards)
        {
            card.IsSelected = card.Key == _selectedCardKey;
            card.IsAssignSubject = card.Key == _assignCardKey;
            card.ShowCheckBox = assignFactKey is not null;
            if (assignFactKey is not null)
            {
                var existing = card.ExistingTargets.Contains(assignFactKey);
                card.IsChecked = existing
                    || card.PendingTargets.Contains(assignFactKey);
                card.IsCheckEnabled = !existing;
            }
        }

        OnChanged(nameof(InAssignMode));
        OnChanged(nameof(AssignHint));
        LinksChanged?.Invoke();
    }

    /// <summary>Card list = find card + one card per distinct citation,
    /// in first appearance order over the fact rows. In-place sync so
    /// selection and post-upload refreshes keep their cards.</summary>
    private void SyncSourceCards()
    {
        var desired = new List<(string Key, Action<SourceCardVM> Update)>
        {
            ("find", UpdateFindCard),
        };
        var seen = new HashSet<string>();
        foreach (var fact in Facts)
            foreach (var reference in fact.Citations)
                if (seen.Add(reference.Handle))
                {
                    var captured = reference;
                    desired.Add((reference.Handle,
                                 card => UpdateCitationCard(card, captured)));
                }

        var same = SourceCards.Count == desired.Count && SourceCards
            .Zip(desired).All(pair => pair.First.Key == pair.Second.Key);
        if (same)
        {
            foreach (var (card, want) in SourceCards.Zip(desired))
                want.Update(card);
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

    private void UpdateFindCard(SourceCardVM card)
    {
        card.Title = string.IsNullOrWhiteSpace(Abbrev) ? SourceTitle : Abbrev;
        card.Page = CitationPage;
        card.ToolTipText = string.Join("\n", new[]
        {
            SourceTitle,
            SourceAuthor,
            CallNumber is { Length: > 0 } ? "Signatur: " + CallNumber : "",
            CitationPage,
            CitationDate,
        }.Where(line => line.Length > 0));
    }

    private static void UpdateCitationCard(SourceCardVM card, CitationRef reference)
    {
        card.Title = reference.SourceLabel;
        card.Page = reference.Page ?? "";
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
                if (card.ExistingTargets.Contains(fact.Handle))
                    pairs.Add((fact, card, false));
                else if (card.PendingTargets.Contains(fact.Handle))
                    pairs.Add((fact, card, true));
            }
            return pairs;
        }
        var anchor = FactByKey(_assignFactKey ?? _selectedFactKey);
        if (anchor is not null)
        {
            foreach (var sourceCard in SourceCards)
            {
                if (sourceCard.ExistingTargets.Contains(anchor.Handle))
                    pairs.Add((anchor, sourceCard, false));
                else if (sourceCard.PendingTargets.Contains(anchor.Handle))
                    pairs.Add((anchor, sourceCard, true));
            }
        }
        return pairs;
    }

    // ---- find form (stands in for scraped data) ----------------------

    private string _repoName = "Matricula Online";
    public string RepoName { get => _repoName; set => Set(ref _repoName, value); }

    private string _repoUrl = "https://data.matricula-online.eu/";
    public string RepoUrl { get => _repoUrl; set => Set(ref _repoUrl, value); }

    private string _sourceTitle = "Testpfarrei, Taufbuch Bd. 1 (1770-1790)";
    public string SourceTitle
    {
        get => _sourceTitle;
        set
        {
            if (!Set(ref _sourceTitle, value))
                return;
            if (DeriveKey)
                SourceKey = DeriveSourceKey(value);
            if (DeriveAbbrev)
                Abbrev = DeriveAbbrevText(value);
            RefreshLinkView();   // the find card shows the title
        }
    }

    private string _sourceAuthor = "Kath. Pfarramt Testpfarrei";
    public string SourceAuthor { get => _sourceAuthor; set => Set(ref _sourceAuthor, value); }

    // fixed derivation for now (real client: "Pfarrei, Buchtyp Von–Bis");
    // a user-configurable format can come later
    private string _abbrev = "";
    public string Abbrev
    {
        get => _abbrev;
        set { if (Set(ref _abbrev, value)) RefreshLinkView(); }
    }

    private bool _deriveAbbrev = true;
    public bool DeriveAbbrev
    {
        get => _deriveAbbrev;
        set
        {
            if (Set(ref _deriveAbbrev, value))
            {
                OnChanged(nameof(AbbrevIsManual));
                if (value)
                    Abbrev = DeriveAbbrevText(SourceTitle);
            }
        }
    }

    public bool AbbrevIsManual => !DeriveAbbrev;

    private string _sourceKey = "";
    public string SourceKey
    {
        get => _sourceKey;
        set
        {
            if (Set(ref _sourceKey, value) && DerivePermalink)
                Permalink = ComputePermalink();
        }
    }

    private bool _deriveKey = true;
    public bool DeriveKey
    {
        get => _deriveKey;
        set
        {
            if (Set(ref _deriveKey, value))
            {
                OnChanged(nameof(KeyIsManual));
                if (value)
                    SourceKey = DeriveSourceKey(SourceTitle);
            }
        }
    }

    public bool KeyIsManual => !DeriveKey;

    private string _callNumber = "T 1/1";
    public string CallNumber { get => _callNumber; set => Set(ref _callNumber, value); }

    private string _citationPage = "S. 42, Eintrag 7";
    public string CitationPage
    {
        get => _citationPage;
        set
        {
            if (!Set(ref _citationPage, value))
                return;
            if (DerivePermalink)
                Permalink = ComputePermalink();
            RefreshLinkView();   // the find card shows the page
        }
    }

    private string _citationDate = "1780-03-12";
    public string CitationDate { get => _citationDate; set => Set(ref _citationDate, value); }

    public string[] ConfidenceValues { get; } =
        ["very_low", "low", "normal", "high", "very_high"];

    private string _confidence = "normal";
    public string Confidence { get => _confidence; set => Set(ref _confidence, value); }

    private string _permalink = "";
    public string Permalink { get => _permalink; set => Set(ref _permalink, value); }

    private bool _derivePermalink = true;
    public bool DerivePermalink
    {
        get => _derivePermalink;
        set
        {
            if (Set(ref _derivePermalink, value))
            {
                OnChanged(nameof(PermalinkIsManual));
                if (value)
                    Permalink = ComputePermalink();
            }
        }
    }

    public bool PermalinkIsManual => !DerivePermalink;

    private string ComputePermalink() =>
        $"https://data.matricula-online.eu/de/{SourceKey}/{DeriveSourceKey(CitationPage)}";

    private string _noteText = "Transkription: ...";
    public string NoteText { get => _noteText; set => Set(ref _noteText, value); }

    private bool _copyLinkToPersons = true;
    public bool CopyLinkToPersons { get => _copyLinkToPersons; set => Set(ref _copyLinkToPersons, value); }

    private FindSnapshot SnapshotFind() => new()
    {
        RepoName = RepoName, RepoUrl = RepoUrl,
        SourceTitle = SourceTitle, SourceAuthor = SourceAuthor,
        Abbrev = Abbrev, SourceKey = SourceKey, CallNumber = CallNumber,
        Page = CitationPage, DateText = CitationDate,
        Confidence = Confidence, Permalink = Permalink,
        NoteText = NoteText, CopyLinkToPersons = CopyLinkToPersons,
    };

    // ---- new-event draft controls -----------------------------------

    /// <summary>Grouped like the Gramps event editor, fetched from
    /// GET /event-types on connect (incl. the tree's custom types and
    /// the is_family flag — no hardcoded lists).</summary>
    public ObservableCollection<EventTypeChoice> EventTypeChoices { get; } = [];

    /// <summary>Preselected in the next dialog (last chosen type —
    /// series of same-type finds are the normal case).</summary>
    private EventTypeChoice? _lastEventType;

    private async Task LoadEventTypesAsync()
    {
        if (_client is null)
            return;
        try
        {
            var catalog = await _client.GetEventTypesAsync();
            var keepXml = _lastEventType?.Xml ?? "Baptism";
            EventTypeChoices.Clear();
            foreach (var group in catalog.Groups)
                foreach (var type in group.Types)
                    EventTypeChoices.Add(new EventTypeChoice(
                        group.Name, type.Xml, type.Label, type.IsFamily));
            foreach (var custom in catalog.Custom)
                EventTypeChoices.Add(new EventTypeChoice(
                    "Benutzerdefiniert", custom, custom, IsFamily: false));
            _lastEventType =
                EventTypeChoices.FirstOrDefault(t => t.Xml == keepXml);
        }
        catch (Exception ex)
        {
            // e.g. NO_TREE_OPEN — the dialog stays unavailable until reconnect
            QueueStatus = "Ereignistypen nicht ladbar: " + ex.Message;
        }
    }

    private void OpenAddEventDialog()
    {
        if (_centerNode is null || EventTypeChoices.Count == 0)
            return;
        // prefill = the open record's date (right for the primary event,
        // a Taufe from the Taufbuch); freely editable incl. qualifiers
        // for derived mentions ("Mutter bereits verstorben" -> vor 1757)
        var dialog = new EventTypeDialog(EventTypeChoices, _lastEventType,
                                         CitationDate)
        {
            Owner = System.Windows.Application.Current.MainWindow,
        };
        if (dialog.ShowDialog() != true || dialog.SelectedType is not { } choice)
            return;
        _lastEventType = choice;
        var date = ParseDate(dialog.DateText);
        if (date is not null)
            date.Type = dialog.DateType;
        AddPendingEvent(choice, dialog.Description, date, dialog.DateDisplay);
    }

    public ICommand AddEventCommand { get; }

    // ---- the change list ("Änderungsliste") --------------------------

    public ObservableCollection<ChangeEntry> Changes { get; } = [];
    public ObservableCollection<ChangeGroupVM> ChangeTree { get; } = [];

    private string _queueStatus = "";
    public string QueueStatus { get => _queueStatus; set => Set(ref _queueStatus, value); }

    public ICommand SendCommand { get; }
    public ICommand DeleteEntryCommand { get; }
    public ICommand DeleteGroupCommand { get; }

    /// <summary>"+ Ereignis vormerken" for whatever person is centered.
    /// Virtual centers get pending-person events (uploaded together
    /// with their create_person capture); family events on a virtual
    /// family become pending-family events that run once the family
    /// has materialized.</summary>
    private void AddPendingEvent(EventTypeChoice eventType, string description,
                                 DateSpec? eventDate, string eventDateText)
    {
        if (_centerNode is null)
            return;
        var center = _centerNode;

        if (eventType.IsFamily)
        {
            var family = SelectedFamily?.Family;
            if (family is null)
            {
                QueueStatus = $"{eventType.Label} ist ein Familienereignis — "
                              + "keine Familie vorhanden";
                return;
            }
            var entityLabel = "Familie: " + center.DisplayName
                + (family.PartnerOf(center) is { } partner
                   ? " ⚭ " + partner.DisplayName : "")
                + (family.IsVirtual ? " (neu)" : "");
            Changes.Add(new ChangeEntry
            {
                Kind = ChangeKind.CreateEvent,
                Find = SnapshotFind(),
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
            AfterChangesMutation(
                $"Änderung erfasst: {eventType.Label} ({entityLabel})");
            return;
        }

        if (center.IsVirtual)
        {
            if (Changes.FirstOrDefault(c => c.Id == center.EntryId)
                    is not { } personEntry)
                return;
            Changes.Add(new ChangeEntry
            {
                Kind = ChangeKind.CreateEvent,
                Find = SnapshotFind(),
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
                $"Änderung erfasst: {eventType.Label} für {personEntry.EntityLabel}");
            return;
        }

        Changes.Add(new ChangeEntry
        {
            Kind = ChangeKind.CreateEvent,
            Find = SnapshotFind(),
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
        AfterChangesMutation($"Änderung erfasst: neues Ereignis {eventType.Label}");
    }

    private (string Key, string Label) FactEntity(FactRowVM fact)
    {
        // pending rows group under their own entity (e.g. the virtual
        // person's root in the change tree), not under the center
        if (fact.IsPendingNew
            && Changes.FirstOrDefault(c => c.Id == fact.Handle) is { } pending)
            return (pending.EntityKey, pending.EntityLabel);
        if (fact.Scope == "family" && fact.FamilyHandle is { } familyHandle)
        {
            var partner = _centerNode is not null
                ? _graph.Family(familyHandle)?.PartnerOf(_centerNode) : null;
            var label = "Familie: " + (_centerNode?.DisplayName ?? "?")
                + (partner is not null ? " ⚭ " + partner.DisplayName : "");
            return (familyHandle, label);
        }
        return (_centerNode?.Id ?? "?", _centerNode?.DisplayName ?? "?");
    }

    private void DeleteEntry(ChangeEntry entry)
    {
        var doomed = CollectWithDependents(entry);
        if (doomed.Count > 1)
        {
            var answer = System.Windows.MessageBox.Show(
                $"Entfernt auch {doomed.Count - 1} abhängige Änderung(en). Fortfahren?",
                "Änderung löschen", System.Windows.MessageBoxButton.YesNo,
                System.Windows.MessageBoxImage.Question);
            if (answer != System.Windows.MessageBoxResult.Yes)
                return;
        }
        RemoveEntriesAndNodes(doomed);
        AfterChangesMutation($"{doomed.Count} Änderung(en) entfernt");
    }

    private void DeleteGroup(ChangeGroupVM group)
    {
        var doomed = Changes.Where(c => c.EntityKey == group.EntityKey).ToList();
        var answer = System.Windows.MessageBox.Show(
            $"Alle {doomed.Count} Änderung(en) für \"{group.EntityLabel}\" löschen?",
            "Änderungen löschen", System.Windows.MessageBoxButton.YesNo,
            System.Windows.MessageBoxImage.Question);
        if (answer != System.Windows.MessageBoxResult.Yes)
            return;
        RemoveEntriesAndNodes(doomed);
        AfterChangesMutation($"{doomed.Count} Änderung(en) entfernt");
    }

    /// <summary>Removes the entries and, for create-person entries,
    /// their nodes from the graph. Pruning may orphan further virtual
    /// persons (children of a deleted virtual couple) — their entries
    /// go too. A deleted virtual center falls back to the last real
    /// person.</summary>
    private void RemoveEntriesAndNodes(List<ChangeEntry> doomed)
    {
        var queue = new Queue<ChangeEntry>(doomed);
        while (queue.Count > 0)
        {
            var entry = queue.Dequeue();
            Changes.Remove(entry);
            if (entry.Kind != ChangeKind.CreatePerson
                || _graph.Person("new:" + entry.Id) is not { } node)
                continue;
            foreach (var orphan in _graph.RemoveVirtualPerson(node))
                if (Changes.FirstOrDefault(c => c.Id == orphan.EntryId)
                        is { } orphanEntry)
                    foreach (var dependent in CollectWithDependents(orphanEntry))
                        queue.Enqueue(dependent);
        }
        // family events whose (virtual) family vanished with the pruning
        foreach (var stale in Changes.Where(c =>
                     c is { Kind: ChangeKind.CreateEvent,
                            OwnerKind: "pending-family" }
                     && _graph.Family(c.OwnerHandle) is null).ToList())
            foreach (var dependent in CollectWithDependents(stale))
                Changes.Remove(dependent);

        if (_centerNode is { IsVirtual: true } center
            && _graph.Person(center.Id) is null)
        {
            _centerNode = null;
            if (_lastRealCenterId is { } real)
                _ = LoadCenterAsync(real);
        }
    }

    private List<ChangeEntry> CollectWithDependents(ChangeEntry root)
    {
        var result = new List<ChangeEntry> { root };
        for (var i = 0; i < result.Count; i++)
            result.AddRange(Changes.Where(c => c.DependsOnId == result[i].Id
                                               && !result.Contains(c)));
        return result;
    }

    private void AfterChangesMutation(string status)
    {
        RebuildFamilyCombo();
        RebuildRows();
        SyncFacts();
        RecomputeBadges();
        RebuildChangeTree();
        RefreshLinkView();
        QueueStatus = $"{status} — {Changes.Count} Änderung(en) offen";
    }

    private void RebuildChangeTree()
    {
        ChangeTree.Clear();
        foreach (var groupEntries in Changes.GroupBy(c => c.EntityKey))
        {
            var group = new ChangeGroupVM
            {
                EntityKey = groupEntries.Key,
                EntityLabel = groupEntries.First().EntityLabel,
            };
            var nodes = groupEntries.ToDictionary(c => c.Id, c => new ChangeNodeVM(c));
            foreach (var entry in groupEntries)
            {
                if (entry.DependsOnId is { } parent
                    && nodes.TryGetValue(parent, out var parentNode))
                    parentNode.Children.Add(nodes[entry.Id]);
                else
                    group.Children.Add(nodes[entry.Id]);
            }
            ChangeTree.Add(group);
        }
    }

    private void RecomputeBadges()
    {
        foreach (var fact in Facts)
            fact.PendingCount = Changes.Count(c =>
                c.Kind is ChangeKind.AttachCitation or ChangeKind.AttachExisting
                && c.TargetHandle == fact.Handle);
        foreach (var box in AllBoxes())
            box.PendingCount = box.IsVirtual
                ? Changes.Count(c =>
                    c.Kind == ChangeKind.CreateEvent
                    && c.OwnerKind == "pending-person"
                    && "new:" + c.OwnerHandle == box.Handle)
                : Changes.Count(c =>
                    c.Kind == ChangeKind.CreateEvent
                    && c.OwnerKind == "person" && c.OwnerHandle == box.Handle);
    }

    // ---- upload (one capture-batch = ONE transaction) ----------------

    private static RepositoryBlock RepoBlock(FindSnapshot find) => new()
    {
        Match = new MatchSpec { By = "name", Value = find.RepoName },
        CreateIfMissing = new RepositoryCreate
        {
            Name = find.RepoName,
            Type = "Website",
            Url = NullIfEmpty(find.RepoUrl),
        },
    };

    private static SourceBlock SourceBlockOf(FindSnapshot find) => new()
    {
        Match = new MatchSpec
        {
            By = "attribute", Key = "MH_SourceKey", Value = find.SourceKey,
        },
        CreateIfMissing = new SourceCreate
        {
            Title = find.SourceTitle,
            Author = NullIfEmpty(find.SourceAuthor),
            Abbreviation = NullIfEmpty(find.Abbrev),
            Attributes = [new AttributeKV("MH_SourceKey", find.SourceKey)],
            RepositoryRef = new RepoRefSpec
            {
                CallNumber = NullIfEmpty(find.CallNumber),
                MediaType = "Book",
            },
        },
    };

    private static CitationBlock CitationBlockOf(FindSnapshot find) => new()
    {
        Page = NullIfEmpty(find.Page),
        Date = ParseDate(find.DateText),
        Confidence = find.Confidence,
        Attributes = string.IsNullOrWhiteSpace(find.Permalink)
            ? null
            : [new AttributeKV("MH_Permalink", find.Permalink)],
        Notes = string.IsNullOrWhiteSpace(find.NoteText)
            ? null
            : [new NoteSpec { Type = "Citation", Text = find.NoteText }],
    };

    /// <summary>Serializes the change list + the virtual subgraph into
    /// one batch: bare persons, family links (new families and member
    /// additions to real ones), events, one citation per find covering
    /// all its targets, plus existing-citation attaches. No client-side
    /// ordering logic — the addon resolves the references in a fixed
    /// safe order inside ONE transaction.</summary>
    private BatchRequest BuildBatch()
    {
        var request = new BatchRequest { RequestId = Guid.NewGuid().ToString() };

        foreach (var entry in Changes.Where(c => c.Kind == ChangeKind.CreatePerson))
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
                // a real family only appears when it gains virtual members
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
                    spec.Children = virtualChildren;
                if (spec.Father is not null || spec.Mother is not null
                    || spec.Children is not null)
                    request.Families.Add(spec);
            }
        }

        foreach (var entry in Changes.Where(c => c.Kind == ChangeKind.CreateEvent))
        {
            var isPerson = entry.OwnerKind is "person" or "pending-person";
            var ownerRef = entry.OwnerKind == "pending-person"
                ? "new:" + entry.OwnerHandle
                : entry.OwnerHandle!;
            request.Events.Add(new BatchEventSpec
            {
                Tmp = "evt:" + entry.Id,
                Type = entry.EventType!,
                Person = isPerson ? ownerRef : null,
                Family = isPerson ? null : ownerRef,
                // the event's OWN date (user-entered, may be qualified
                // "vor 1757") — the record's date stays on the citation
                Date = entry.EventDate,
                Description = entry.EventDescription,
            });
        }

        // one citation per find (record), attached to everything it
        // evidences: explicit attach targets + the find's new events
        foreach (var group in Changes
                     .Where(c => c.Kind is ChangeKind.AttachCitation
                                 or ChangeKind.CreateEvent)
                     .GroupBy(c => c.Find!.GroupKey))
        {
            var find = group.First().Find!;
            var targets = new List<BatchTargetRef>();
            var seen = new HashSet<(string, string)>();
            string? eventLabel = null;
            foreach (var entry in group)
            {
                var (type, reference) = entry.Kind == ChangeKind.CreateEvent
                    ? ("event", "evt:" + entry.Id)
                    : entry.TargetKind == "pending-event"
                        ? ("event", "evt:" + entry.TargetHandle)
                        : (entry.TargetKind!, entry.TargetHandle!);
                if (entry.Kind == ChangeKind.CreateEvent)
                    eventLabel ??= entry.EventTypeLabel ?? entry.EventType;
                if (seen.Add((type, reference)))
                    targets.Add(new BatchTargetRef { Type = type, Ref = reference });
            }
            request.Citations.Add(new BatchCitationSpec
            {
                Repository = RepoBlock(find),
                Source = SourceBlockOf(find),
                Citation = CitationBlockOf(find),
                Targets = targets,
                PersonUrl = find.CopyLinkToPersons
                            && !string.IsNullOrWhiteSpace(find.Permalink)
                    ? new PersonUrlSpec
                    {
                        Path = find.Permalink.Trim(),
                        Description = eventLabel is not null
                            ? $"{eventLabel} {find.DateText}"
                            : "Beleg " + find.Page,
                        Type = "Digitalisat",
                    }
                    : null,
            });
        }

        foreach (var group in Changes
                     .Where(c => c.Kind == ChangeKind.AttachExisting)
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

    /// <summary>Sends the whole change list as ONE capture-batch = ONE
    /// Gramps transaction: either everything lands (a single undo in
    /// Gramps reverts it all) or nothing does and every entry stays in
    /// the list, marked with the error.</summary>
    private async Task SendAllAsync()
    {
        if (_client is null || Changes.Count == 0)
            return;
        BatchRequest request;
        try
        {
            request = BuildBatch();
        }
        catch (Exception ex)
        {
            QueueStatus = "Stapel nicht erstellbar: " + ex.Message;
            LogNote("upload", "Stapel nicht erstellbar: " + ex.Message);
            return;
        }
        LogNote("upload", $"Stapel: {request.Persons.Count} Person(en), "
            + $"{request.Families.Count} Familie(n), "
            + $"{request.Events.Count} Ereignis(se), "
            + $"{request.Citations.Count} Zitat(e), "
            + $"{request.Attach.Count} Zitat-Anhänge");
        try
        {
            Log("capture-batch request", request);
            var response = await _client.CaptureBatchAsync(request);
            Log("capture-batch response", response);

            // temp ids -> real handles, node identity kept (no view jump)
            foreach (var (tmp, created) in response.Created.Persons)
                if (_graph.Person(tmp) is { } node)
                    _graph.ReplacePersonId(node, created.Handle);
            foreach (var (tmp, created) in response.Created.Families)
                if (_graph.Family(tmp) is { } family)
                    _graph.ReplaceFamilyId(family, created.Handle);

            var count = Changes.Count;
            Changes.Clear();
            // spec 7.3: read back the displayed slice, without moving
            // the view — a just-uploaded virtual center carries its
            // real handle by now
            if (_centerNode is not null)
                await LoadCenterAsync(_centerNode.Id);
            AfterChangesMutation(
                $"{count} Änderung(en) in EINER Transaktion ausgeführt "
                + "(ein Undo in Gramps)");
        }
        catch (Exception ex)
        {
            var message = ex is BridgeException bridgeEx
                ? $"{bridgeEx.Code}: {bridgeEx.Message}" : ex.Message;
            foreach (var entry in Changes)
                entry.Error = "Stapel fehlgeschlagen: " + message;
            LogNote("capture-batch FEHLER", message);
            QueueStatus = "Upload fehlgeschlagen — nichts geschrieben "
                + "(eine Transaktion): " + message;
        }
    }

    // ---- log ---------------------------------------------------------
    //
    // Everything in the log panel also goes to a session logfile —
    // client-side blocks (unresolvable links, missing prerequisites)
    // never reach the bridge, so without the file they left no trace.

    private static readonly string s_logFile = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Matrikelhelfer", "GrampsBridgeTester.log");

    private bool _logFileStarted;

    private string _logText = "Logdatei: " + s_logFile + "\n\n";
    public string LogText { get => _logText; set => Set(ref _logText, value); }

    private void Log(string title, object payload) =>
        AppendLog(title, JsonSerializer.Serialize(payload, BridgeJson.Options));

    private void LogNote(string title, string text) => AppendLog(title, text);

    private void AppendLog(string title, string body)
    {
        var block = $"---- {DateTime.Now:HH:mm:ss} {title} ----\n{body}\n\n";
        LogText = block + LogText;
        try
        {
            System.IO.Directory.CreateDirectory(
                System.IO.Path.GetDirectoryName(s_logFile)!);
            if (!_logFileStarted)
            {
                System.IO.File.WriteAllText(s_logFile,
                    $"==== GrampsBridgeTester {DateTime.Now:yyyy-MM-dd HH:mm:ss} ====\n\n");
                _logFileStarted = true;
            }
            System.IO.File.AppendAllText(s_logFile, block);
        }
        catch (Exception)
        {
            // best-effort: a log that cannot be written never fails the app
        }
    }

    // ---- helpers -----------------------------------------------------

    private static string DeriveSourceKey(string title)
    {
        var text = title.ToLowerInvariant()
            .Replace("ä", "ae").Replace("ö", "oe").Replace("ü", "ue")
            .Replace("ß", "ss");
        text = new string(text.Normalize(System.Text.NormalizationForm.FormD)
            .Where(c => System.Globalization.CharUnicodeInfo.GetUnicodeCategory(c)
                        != System.Globalization.UnicodeCategory.NonSpacingMark)
            .ToArray());
        var builder = new System.Text.StringBuilder(text.Length);
        var pendingDash = false;
        foreach (var c in text)
        {
            if (char.IsAsciiLetterOrDigit(c))
            {
                if (pendingDash && builder.Length > 0)
                    builder.Append('-');
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

    /// <summary>Fixed abbreviation derivation (tester stand-in): title
    /// minus parenthesized parts. The real client derives from the
    /// scraped fields ("Pfarrei, Buchtyp Von–Bis").</summary>
    private static string DeriveAbbrevText(string title)
    {
        var text = System.Text.RegularExpressions.Regex
            .Replace(title, @"\s*\([^)]*\)", "");
        text = System.Text.RegularExpressions.Regex
            .Replace(text, @"\s+", " ").Trim();
        return text.Length > 60 ? text[..60].TrimEnd() : text;
    }

    private static string? NullIfEmpty(string value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static DateSpec? ParseDate(string value)
    {
        var parts = value.Trim().Split('-');
        if (parts.Length == 0 || !int.TryParse(parts[0], out var year))
            return null;
        var spec = new DateSpec { Type = "regular", Year = year };
        if (parts.Length > 1 && int.TryParse(parts[1], out var month))
            spec.Month = month;
        if (parts.Length > 2 && int.TryParse(parts[2], out var day))
            spec.Day = day;
        return spec;
    }
}
