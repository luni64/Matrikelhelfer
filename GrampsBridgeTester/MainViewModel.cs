using System.Collections.ObjectModel;
using System.Text.Json;
using System.Windows.Input;
using GrampsBridge;

namespace GrampsBridgeTester;

/// <summary>
/// Prototype of the "Gramps-Modus" view (spec 7.3): search, walkable
/// mini-tree, and a change list ("Änderungsliste") — every user action
/// (drop a citation, add an event) is recorded as a deletable entry;
/// upload executes the list with dependency ordering and shared-citation
/// coalescing, then reads the displayed slice back in place.
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
        AddEventCommand = new RelayCommand(AddPendingEvent,
                                           () => Center is not null);
        NavigateCommand = new RelayCommand<PersonBoxVM>(
            async void (box) => await LoadCenterAsync(box.Handle),
            box => !box.IsPlaceholder);
        ToggleFactCommand = new RelayCommand<FactRowVM>(DropFindOnFact);
        DeleteEntryCommand = new RelayCommand<ChangeEntry>(DeleteEntry);
        DeleteGroupCommand = new RelayCommand<ChangeGroupVM>(DeleteGroup);
        SourceKey = DeriveSourceKey(SourceTitle);
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
    public ICommand ToggleFactCommand { get; }

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
            if (Set(ref _sourceTitle, value) && DeriveKey)
                SourceKey = DeriveSourceKey(value);
        }
    }

    private string _sourceAuthor = "Kath. Pfarramt Testpfarrei";
    public string SourceAuthor { get => _sourceAuthor; set => Set(ref _sourceAuthor, value); }

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
            if (Set(ref _citationPage, value) && DerivePermalink)
                Permalink = ComputePermalink();
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
        SourceKey = SourceKey, CallNumber = CallNumber,
        Page = CitationPage, DateText = CitationDate,
        Confidence = Confidence, Permalink = Permalink,
        NoteText = NoteText, CopyLinkToPersons = CopyLinkToPersons,
    };

    // ---- new-event draft controls -----------------------------------

    public string[] EventTypes { get; } =
        ["Baptism", "Christening", "Birth", "Marriage", "Death", "Burial",
         "Occupation", "Residence"];

    private static readonly HashSet<string> s_familyEventTypes =
        ["Marriage", "Marriage Banns", "Engagement", "Divorce"];

    private string _eventType = "Baptism";
    public string EventType { get => _eventType; set => Set(ref _eventType, value); }

    private string _eventDescription = "";
    public string EventDescription { get => _eventDescription; set => Set(ref _eventDescription, value); }

    public ICommand AddEventCommand { get; }

    // ---- the change list ("Änderungsliste") --------------------------

    public ObservableCollection<ChangeEntry> Changes { get; } = [];
    public ObservableCollection<ChangeGroupVM> ChangeTree { get; } = [];

    private string _queueStatus = "";
    public string QueueStatus { get => _queueStatus; set => Set(ref _queueStatus, value); }

    public ICommand SendCommand { get; }
    public ICommand DeleteEntryCommand { get; }
    public ICommand DeleteGroupCommand { get; }

    /// <summary>Drop on a person box: record "citation → person".</summary>
    public void DropFindOnPerson(PersonBoxVM box)
    {
        var find = SnapshotFind();
        if (HasDuplicate(find, "person", box.Handle))
            return;
        Changes.Add(new ChangeEntry
        {
            Kind = ChangeKind.AttachCitation,
            Find = find,
            EntityKey = box.Handle,
            EntityLabel = box.Name,
            TargetKind = "person",
            TargetHandle = box.Handle,
            TargetLabel = box.Name,
        });
        AfterChangesMutation($"Änderung erfasst: Zitat {find.Page} → {box.Name}");
    }

    /// <summary>Drop on a fact row (real or pending event).</summary>
    public void DropFindOnFact(FactRowVM fact)
    {
        var find = SnapshotFind();
        var targetKind = fact.IsPendingNew ? "pending-event" : "event";
        if (HasDuplicate(find, targetKind, fact.Handle))
            return;
        var (entityKey, entityLabel) = FactEntity(fact);
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
        AfterChangesMutation($"Änderung erfasst: Zitat {find.Page} → {fact.Label}");
    }

    /// <summary>"+ Ereignis vormerken": record a create-event change; a
    /// pending row appears in the facts list and is itself a drop target.</summary>
    private void AddPendingEvent()
    {
        if (Center is null)
            return;
        var find = SnapshotFind();
        string ownerKind, ownerHandle, entityLabel;
        if (s_familyEventTypes.Contains(EventType))
        {
            if (SelectedFamily is null)
            {
                QueueStatus = $"{EventType} ist ein Familienereignis — "
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
            EventType = EventType,
            OwnerKind = ownerKind,
            OwnerHandle = ownerHandle,
            EventDescription = NullIfEmpty(EventDescription),
        });
        AfterChangesMutation($"Änderung erfasst: neues Ereignis {EventType}");
    }

    private bool HasDuplicate(FindSnapshot find, string targetKind, string handle)
    {
        var duplicate = Changes.Any(c =>
            c.Kind == ChangeKind.AttachCitation
            && c.Find.GroupKey == find.GroupKey
            && c.TargetKind == targetKind && c.TargetHandle == handle);
        if (duplicate)
            QueueStatus = "bereits vorgemerkt (Eintrag in der Änderungsliste)";
        return duplicate;
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
                c.Kind == ChangeKind.AttachCitation && c.TargetHandle == fact.Handle);
        foreach (var box in AllBoxes())
            box.PendingCount = Changes.Count(c =>
                (c.Kind == ChangeKind.AttachCitation
                 && c.TargetKind == "person" && c.TargetHandle == box.Handle)
                || (c.Kind == ChangeKind.CreateEvent
                    && c.OwnerKind == "person" && c.OwnerHandle == box.Handle));
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
                a.Find.GroupKey == create.Find.GroupKey).ToList();
            var realTargets = sameRecord.Where(a => a.TargetKind is "person" or "event")
                .ToList();
            var redundant = sameRecord.Where(a =>
                a.TargetKind == "pending-event" && a.TargetHandle == create.Id).ToList();
            var covered = new List<ChangeEntry> { create };
            covered.AddRange(realTargets);
            covered.AddRange(redundant);   // create's own citation covers these
            foreach (var entry in realTargets.Concat(redundant))
                attaches.Remove(entry);
            await RunCapture(create.Find,
                realTargets.Select(a => new TargetRef
                { Type = a.TargetKind!, Handle = a.TargetHandle! }).ToList(),
                create, covered);
        }

        // phase B: remaining citation drops, one capture per record
        foreach (var group in attaches.GroupBy(a => a.Find.GroupKey))
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
                await RunCapture(group.First().Find, targets, null, covered);
        }

        // spec 7.3: read back the displayed slice, without moving the view
        if (Center is not null)
            await LoadCenterAsync(Center.Handle);
        SyncFacts();
        RecomputeBadges();
        RebuildChangeTree();
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
