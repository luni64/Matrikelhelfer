using System.Collections.ObjectModel;
using System.Text.Json;
using System.Windows.Input;
using GrampsBridge;

namespace GrampsBridgeTester;

/// <summary>
/// Prototype of the "Gramps-Modus" view (spec 7.3): search, walkable
/// mini-tree, an Ancestry-style event↔source link view (click draws
/// connector lines, double-click enters assign mode) and a change list
/// ("Änderungsliste") — every user action (assign a citation, add an
/// event) is recorded as a deletable entry; upload executes the list
/// with dependency ordering and shared-citation coalescing, then reads
/// the displayed slice back in place.
/// </summary>
public sealed class MainViewModel : ObservableObject
{
    private BridgeClient? _client;
    private bool _suppressFamilyReload;
    private List<PersonEvent> _serverEvents = [];

    public MainViewModel()
    {
        DiscoveryPath = Discovery.DefaultPath;
        ConnectCommand = new RelayCommand(async void () => await ConnectAsync());
        SearchCommand = new RelayCommand(async void () => await SearchAsync(),
                                         () => _client is not null);
        SendCommand = new RelayCommand(async void () => await SendAllAsync(),
            () => _client is not null && Changes.Count > 0);
        AddEventCommand = new RelayCommand(OpenAddEventDialog,
            () => Center is not null && EventTypeChoices.Count > 0);
        NavigateCommand = new RelayCommand<PersonBoxVM>(
            async void (box) => await LoadCenterAsync(box.Handle),
            box => !box.IsPlaceholder);
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

    // ---- walkable tree ----------------------------------------------

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
    public ObservableCollection<FamilyInfo> Families { get; } = [];

    public bool HasMultipleFamilies => Families.Count > 1;

    private List<PersonBrief> _centerParents = [];
    private string _centerGender = "U";

    private FamilyInfo? _selectedFamily;
    public FamilyInfo? SelectedFamily
    {
        get => _selectedFamily;
        set
        {
            if (Set(ref _selectedFamily, value) && !_suppressFamilyReload)
                _ = UpdateFamilyDependentAsync();
        }
    }

    public ICommand NavigateCommand { get; }

    private async Task LoadCenterAsync(string handle)
    {
        if (_client is null)
            return;
        try
        {
            var detail = await _client.GetPersonAsync(handle);

            if (Center?.Handle != handle)
                Center = new PersonBoxVM(handle) { IsCenter = true, IsLarge = true };
            Center.UpdateFrom(new PersonBrief(handle, detail.GrampsId,
                detail.PrimaryName, detail.Gender, detail.Birth, detail.Death));

            _centerParents = detail.Parents;
            _centerGender = detail.Gender;
            _serverEvents = detail.Events;
            SyncFacts();

            _suppressFamilyReload = true;
            var keepHandle = SelectedFamily?.Handle;
            Families.Clear();
            foreach (var family in detail.Families)
                Families.Add(family);
            SelectedFamily = Families.FirstOrDefault(f => f.Handle == keepHandle)
                             ?? Families.FirstOrDefault();
            _suppressFamilyReload = false;
            OnChanged(nameof(HasMultipleFamilies));
            await UpdateFamilyDependentAsync();

            RecomputeBadges();
            RefreshLinkView();
        }
        catch (Exception ex)
        {
            SearchStatus = "load failed: " + ex.Message;
        }
    }

    private async Task UpdateFamilyDependentAsync()
    {
        var spouseBrief = SelectedFamily?.Spouse;
        var spouseGender = "U";
        var spouseParents = new List<PersonBrief>();
        if (spouseBrief is null)
        {
            if (Spouse is null || !Spouse.IsPlaceholder)
                Spouse = PersonBoxVM.Placeholder(large: true);
        }
        else
        {
            if (Spouse?.Handle != spouseBrief.Handle)
                Spouse = new PersonBoxVM(spouseBrief.Handle) { IsLarge = true };
            Spouse.UpdateFrom(spouseBrief);
            spouseGender = spouseBrief.Gender ?? "U";
            try
            {
                // spouse's parents live one detail call away
                var spouseDetail = await _client!.GetPersonAsync(spouseBrief.Handle);
                Spouse.UpdateFrom(new PersonBrief(spouseBrief.Handle,
                    spouseDetail.GrampsId, spouseDetail.PrimaryName,
                    spouseDetail.Gender, spouseDetail.Birth, spouseDetail.Death));
                spouseGender = spouseDetail.Gender;
                spouseParents = spouseDetail.Parents;
            }
            catch (Exception)
            {
                // brief data is good enough for the box
            }
        }

        // man left, woman right; the selected person is only marked blue
        var centerLeft = _centerGender switch
        {
            "M" => true,
            "F" => false,
            _ => spouseGender != "M",
        };
        LeftBox = centerLeft ? Center : Spouse;
        RightBox = centerLeft ? Spouse : Center;
        SyncRow(LeftParentsRow,
                PadCouple(centerLeft ? _centerParents : spouseParents));
        SyncRow(RightParentsRow,
                PadCouple(centerLeft ? spouseParents : _centerParents));

        // children plus one trailing "Neu" slot, like the concept sketch
        var children = (SelectedFamily?.Children ?? [])
            .Cast<PersonBrief?>().Append(null).ToList();
        SyncRow(ChildrenRow, children);
        SyncFacts();
        RecomputeBadges();
        RefreshLinkView();
    }

    /// <summary>Always two slots per parent couple; missing -> "Neu".</summary>
    private static List<PersonBrief?> PadCouple(IReadOnlyList<PersonBrief> briefs) =>
        [briefs.ElementAtOrDefault(0), briefs.ElementAtOrDefault(1)];

    /// <summary>In-place row sync (spec 7.3: identical sets keep their
    /// boxes, so a post-upload refresh never moves the view). A null
    /// brief means a "Neu" placeholder slot.</summary>
    private static void SyncRow(ObservableCollection<PersonBoxVM> row,
                                IReadOnlyList<PersonBrief?> briefs)
    {
        var same = row.Count == briefs.Count && row.Zip(briefs).All(pair =>
            pair.Second is null ? pair.First.IsPlaceholder
                                : pair.First.Handle == pair.Second.Handle);
        if (same)
        {
            foreach (var (box, brief) in row.Zip(briefs))
                if (brief is not null)
                    box.UpdateFrom(brief);
            return;
        }
        row.Clear();
        foreach (var brief in briefs)
        {
            if (brief is null)
            {
                row.Add(PersonBoxVM.Placeholder());
                continue;
            }
            var box = new PersonBoxVM(brief.Handle);
            box.UpdateFrom(brief);
            row.Add(box);
        }
    }

    /// <summary>Facts = server events of the center person + pending
    /// create-event entries whose owner is currently displayed. Pending
    /// rows are keyed by their change-entry id, so they transform into
    /// the real row in place once the upload read-back delivers it.</summary>
    private void SyncFacts()
    {
        var desired = new List<(string Key, Action<FactRowVM> Update)>();
        foreach (var evt in _serverEvents)
            desired.Add((evt.Handle, row => row.UpdateFrom(evt)));
        foreach (var entry in Changes.Where(c => c.Kind == ChangeKind.CreateEvent))
        {
            var visible = entry.OwnerKind == "person"
                ? entry.OwnerHandle == Center?.Handle
                : entry.OwnerHandle == SelectedFamily?.Handle;
            if (visible)
                desired.Add((entry.Id, row => row.UpdateFromPending(entry)));
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
        if (Center is null || EventTypeChoices.Count == 0)
            return;
        var dialog = new EventTypeDialog(EventTypeChoices, _lastEventType)
        {
            Owner = System.Windows.Application.Current.MainWindow,
        };
        if (dialog.ShowDialog() != true || dialog.SelectedType is not { } choice)
            return;
        _lastEventType = choice;
        AddPendingEvent(choice, dialog.Description);
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

    /// <summary>"+ Ereignis vormerken": record a create-event change; a
    /// pending row appears in the facts list and is itself a drop target.</summary>
    private void AddPendingEvent(EventTypeChoice eventType, string description)
    {
        if (Center is null)
            return;
        var find = SnapshotFind();
        string ownerKind, ownerHandle, entityLabel;
        if (eventType.IsFamily)
        {
            if (SelectedFamily is null)
            {
                QueueStatus = $"{eventType.Label} ist ein Familienereignis — "
                              + "keine Familie vorhanden";
                return;
            }
            ownerKind = "family";
            ownerHandle = SelectedFamily.Handle;
            entityLabel = "Familie: " + Center.Name
                + (SelectedFamily.Spouse?.PrimaryName is { } spouse ? " ⚭ " + spouse : "");
        }
        else
        {
            ownerKind = "person";
            ownerHandle = Center.Handle;
            entityLabel = Center.Name;
        }
        Changes.Add(new ChangeEntry
        {
            Kind = ChangeKind.CreateEvent,
            Find = find,
            EntityKey = ownerHandle,
            EntityLabel = entityLabel,
            EventType = eventType.Xml,
            EventTypeLabel = eventType.Label,
            OwnerKind = ownerKind,
            OwnerHandle = ownerHandle,
            EventDescription = NullIfEmpty(description),
        });
        AfterChangesMutation($"Änderung erfasst: neues Ereignis {eventType.Label}");
    }

    private (string Key, string Label) FactEntity(FactRowVM fact)
    {
        if (fact.Scope == "family" && fact.FamilyHandle is { } familyHandle)
        {
            var family = Families.FirstOrDefault(f => f.Handle == familyHandle);
            var label = "Familie: " + (Center?.Name ?? "?")
                + (family?.Spouse?.PrimaryName is { } spouse ? " ⚭ " + spouse : "");
            return (familyHandle, label);
        }
        return (Center?.Handle ?? "?", Center?.Name ?? "?");
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
        foreach (var item in doomed)
            Changes.Remove(item);
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
        foreach (var item in doomed)
            Changes.Remove(item);
        AfterChangesMutation($"{doomed.Count} Änderung(en) entfernt");
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
            box.PendingCount = Changes.Count(c =>
                c.Kind == ChangeKind.CreateEvent
                && c.OwnerKind == "person" && c.OwnerHandle == box.Handle);
    }

    // ---- upload ------------------------------------------------------

    private CaptureRequest BuildRequest(FindSnapshot find, List<TargetRef> targets,
                                        ChangeEntry? createEvent)
    {
        var date = ParseDate(find.DateText);
        var request = new CaptureRequest
        {
            RequestId = Guid.NewGuid().ToString(),
            Repository = new RepositoryBlock
            {
                Match = new MatchSpec { By = "name", Value = find.RepoName },
                CreateIfMissing = new RepositoryCreate
                {
                    Name = find.RepoName,
                    Type = "Website",
                    Url = NullIfEmpty(find.RepoUrl),
                },
            },
            Source = new SourceBlock
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
            },
            Citation = new CitationBlock
            {
                Page = NullIfEmpty(find.Page),
                Date = date,
                Confidence = find.Confidence,
                Attributes = string.IsNullOrWhiteSpace(find.Permalink)
                    ? null
                    : [new AttributeKV("MH_Permalink", find.Permalink)],
                Notes = string.IsNullOrWhiteSpace(find.NoteText)
                    ? null
                    : [new NoteSpec { Type = "Citation", Text = find.NoteText }],
            },
        };
        if (targets.Count > 0)
            request.Targets = targets;
        if (createEvent is not null)
        {
            request.CreateEventIfMissing = new CreateEventBlock
            {
                PersonHandle = createEvent.OwnerKind == "person"
                    ? createEvent.OwnerHandle : null,
                FamilyHandle = createEvent.OwnerKind == "family"
                    ? createEvent.OwnerHandle : null,
                EventType = createEvent.EventType!,
                Date = date,
                Description = createEvent.EventDescription,
            };
        }
        if (find.CopyLinkToPersons && !string.IsNullOrWhiteSpace(find.Permalink))
        {
            request.PersonUrl = new PersonUrlSpec
            {
                Path = find.Permalink.Trim(),
                Description = createEvent is not null
                    ? $"{createEvent.EventType} {find.DateText}"
                    : "Beleg " + find.Page,
                Type = "Digitalisat",
            };
        }
        return request;
    }

    /// <summary>Executes the change list: create-event entries first
    /// (each coalesced with same-record citation drops into ONE capture
    /// sharing the citation), then remaining citation attaches grouped
    /// per record. Successes leave the list; failures stay marked.</summary>
    private async Task SendAllAsync()
    {
        if (_client is null)
            return;
        var creates = Changes.Where(c => c.Kind == ChangeKind.CreateEvent).ToList();
        var attaches = Changes.Where(c => c.Kind == ChangeKind.AttachCitation).ToList();
        var createdEventHandles = new Dictionary<string, string>();
        var succeeded = 0;
        var failed = 0;

        async Task RunCapture(FindSnapshot find, List<TargetRef> targets,
                              ChangeEntry? createEvent, List<ChangeEntry> covered)
        {
            try
            {
                var request = BuildRequest(find, targets, createEvent);
                Log("capture request", request);
                var response = await _client.CaptureAsync(request);
                Log("capture response", response);
                if (createEvent is not null
                    && response.Created.Event?.Handle is { } newHandle)
                    createdEventHandles[createEvent.Id] = newHandle;
                foreach (var entry in covered)
                    Changes.Remove(entry);
                succeeded += covered.Count;
            }
            catch (Exception ex)
            {
                var message = ex is BridgeException bridgeEx
                    ? $"{bridgeEx.Code}: {bridgeEx.Message}" : ex.Message;
                foreach (var entry in covered)
                    entry.Error = message;
                failed += covered.Count;
            }
        }

        // phase A: create events, coalescing same-record citation drops
        foreach (var create in creates)
        {
            var sameRecord = attaches.Where(a =>
                a.Find!.GroupKey == create.Find!.GroupKey).ToList();
            var realTargets = sameRecord.Where(a => a.TargetKind is "person" or "event")
                .ToList();
            var redundant = sameRecord.Where(a =>
                a.TargetKind == "pending-event" && a.TargetHandle == create.Id).ToList();
            var covered = new List<ChangeEntry> { create };
            covered.AddRange(realTargets);
            covered.AddRange(redundant);   // create's own citation covers these
            foreach (var entry in realTargets.Concat(redundant))
                attaches.Remove(entry);
            await RunCapture(create.Find!,
                realTargets.Select(a => new TargetRef
                { Type = a.TargetKind!, Handle = a.TargetHandle! }).ToList(),
                create, covered);
        }

        // phase B: remaining citation drops, one capture per record
        foreach (var group in attaches.GroupBy(a => a.Find!.GroupKey))
        {
            var targets = new List<TargetRef>();
            var covered = new List<ChangeEntry>();
            var blocked = false;
            foreach (var entry in group)
            {
                var handle = entry.TargetKind == "pending-event"
                    ? createdEventHandles.GetValueOrDefault(entry.TargetHandle!)
                    : entry.TargetHandle;
                if (handle is null)
                {
                    entry.Error = "Voraussetzung (neues Ereignis) nicht ausgeführt";
                    blocked = true;
                    continue;
                }
                targets.Add(new TargetRef
                {
                    Type = entry.TargetKind == "pending-event" ? "event" : entry.TargetKind!,
                    Handle = handle,
                });
                covered.Add(entry);
            }
            if (blocked)
                failed += group.Count() - covered.Count;
            if (covered.Count > 0)
                await RunCapture(group.First().Find!, targets, null, covered);
        }

        // phase C: attach EXISTING citations, one bridge call per
        // citation with all its checked targets
        var existingAttaches = Changes
            .Where(c => c.Kind == ChangeKind.AttachExisting).ToList();
        foreach (var group in existingAttaches.GroupBy(c => c.CitationHandle!))
        {
            var targets = new List<TargetRef>();
            var covered = new List<ChangeEntry>();
            foreach (var entry in group)
            {
                var handle = entry.TargetKind == "pending-event"
                    ? createdEventHandles.GetValueOrDefault(entry.TargetHandle!)
                    : entry.TargetHandle;
                if (handle is null)
                {
                    entry.Error = "Voraussetzung (neues Ereignis) nicht ausgeführt";
                    failed++;
                    continue;
                }
                targets.Add(new TargetRef
                {
                    Type = entry.TargetKind == "pending-event"
                        ? "event" : entry.TargetKind!,
                    Handle = handle,
                });
                covered.Add(entry);
            }
            if (covered.Count == 0)
                continue;
            try
            {
                var request = new AttachRequest
                {
                    RequestId = Guid.NewGuid().ToString(),
                    Targets = targets,
                };
                Log("attach request", request);
                var response = await _client.AttachCitationAsync(group.Key, request);
                Log("attach response", response);
                foreach (var entry in covered)
                    Changes.Remove(entry);
                succeeded += covered.Count;
            }
            catch (Exception ex)
            {
                var message = ex is BridgeException bridgeEx
                    ? $"{bridgeEx.Code}: {bridgeEx.Message}" : ex.Message;
                foreach (var entry in covered)
                    entry.Error = message;
                failed += covered.Count;
            }
        }

        // spec 7.3: read back the displayed slice, without moving the view
        if (Center is not null)
            await LoadCenterAsync(Center.Handle);
        SyncFacts();
        RecomputeBadges();
        RebuildChangeTree();
        RefreshLinkView();
        QueueStatus = $"{succeeded} Änderung(en) ausgeführt"
                      + (failed > 0 ? $", {failed} fehlgeschlagen (in der Liste markiert)" : "")
                      + " — Anzeige aus Gramps aktualisiert";
    }

    // ---- log ---------------------------------------------------------

    private string _logText = "";
    public string LogText { get => _logText; set => Set(ref _logText, value); }

    private void Log(string title, object payload)
    {
        var json = JsonSerializer.Serialize(payload, BridgeJson.Options);
        LogText = $"---- {DateTime.Now:HH:mm:ss} {title} ----\n{json}\n\n" + LogText;
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
