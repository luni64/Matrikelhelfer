using System.Collections.ObjectModel;
using System.Text.Json;
using System.Windows.Input;
using GrampsBridge;

namespace GrampsBridgeTester;

/// <summary>
/// Prototype of the "Gramps-Modus" view (spec 7.3): search, walkable
/// mini-tree, click-to-assign staging queue, batch upload with in-place
/// read-back. Manual entry fields stand in for scraped data.
/// </summary>
public sealed class MainViewModel : ObservableObject
{
    private BridgeClient? _client;
    private PersonDetail? _centerDetail;
    private bool _suppressFamilyReload;

    public MainViewModel()
    {
        DiscoveryPath = Discovery.DefaultPath;
        ConnectCommand = new RelayCommand(async void () => await ConnectAsync());
        SearchCommand = new RelayCommand(async void () => await SearchAsync(),
                                         () => _client is not null);
        QueueCommand = new RelayCommand(QueueAssignment,
                                        () => Center is not null);
        SendCommand = new RelayCommand(async void () => await SendAllAsync(),
            () => _client is not null
                  && Queue.Any(a => a.Status == AssignmentStatus.Pending));
        NavigateCommand = new RelayCommand<PersonBoxVM>(
            async void (box) => await LoadCenterAsync(box.Handle),
            box => !box.IsPlaceholder);
        TogglePersonCommand = new RelayCommand<PersonBoxVM>(
            box => { box.IsDraftTarget = !box.IsDraftTarget; },
            box => !box.IsPlaceholder);
        ToggleFactCommand = new RelayCommand<FactRowVM>(
            fact => { fact.IsDraftTarget = !fact.IsDraftTarget; });
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

    // fixed positions: man left, woman right — selecting the bride only
    // moves the blue frame, never swaps boxes
    private PersonBoxVM? _leftBox;
    public PersonBoxVM? LeftBox { get => _leftBox; set => Set(ref _leftBox, value); }

    private PersonBoxVM? _rightBox;
    public PersonBoxVM? RightBox { get => _rightBox; set => Set(ref _rightBox, value); }

    public ObservableCollection<PersonBoxVM> LeftParentsRow { get; } = [];
    public ObservableCollection<PersonBoxVM> RightParentsRow { get; } = [];

    private List<PersonBrief> _centerParents = [];
    private string _centerGender = "U";
    public ObservableCollection<PersonBoxVM> ChildrenRow { get; } = [];
    public ObservableCollection<FactRowVM> Facts { get; } = [];
    public ObservableCollection<FamilyInfo> Families { get; } = [];

    public bool HasMultipleFamilies => Families.Count > 1;

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
    public ICommand TogglePersonCommand { get; }
    public ICommand ToggleFactCommand { get; }

    private async Task LoadCenterAsync(string handle)
    {
        if (_client is null)
            return;
        try
        {
            var detail = await _client.GetPersonAsync(handle);
            _centerDetail = detail;

            if (Center?.Handle != handle)
                Center = new PersonBoxVM(handle) { IsCenter = true, IsLarge = true };
            Center.UpdateFrom(new PersonBrief(handle, detail.GrampsId,
                detail.PrimaryName, detail.Gender, detail.Birth, detail.Death));

            _centerParents = detail.Parents;
            _centerGender = detail.Gender;
            SyncFacts(detail.Events);

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

    private void SyncFacts(IReadOnlyList<PersonEvent> events)
    {
        if (Facts.Count == events.Count
            && Facts.Zip(events).All(pair => pair.First.Handle == pair.Second.Handle))
        {
            foreach (var (row, evt) in Facts.Zip(events))
                row.UpdateFrom(evt);
            return;
        }
        Facts.Clear();
        foreach (var evt in events)
        {
            var row = new FactRowVM(evt.Handle);
            row.UpdateFrom(evt);
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

    // ---- new-event draft --------------------------------------------

    public string[] EventTypes { get; } =
        ["Baptism", "Christening", "Birth", "Marriage", "Death", "Burial",
         "Occupation", "Residence"];

    private static readonly HashSet<string> s_familyEventTypes =
        ["Marriage", "Marriage Banns", "Engagement", "Divorce"];

    private bool _draftNewEvent;
    public bool DraftNewEvent { get => _draftNewEvent; set => Set(ref _draftNewEvent, value); }

    private string _eventType = "Baptism";
    public string EventType { get => _eventType; set => Set(ref _eventType, value); }

    private string _eventDescription = "";
    public string EventDescription { get => _eventDescription; set => Set(ref _eventDescription, value); }

    // ---- staging queue ----------------------------------------------

    public ObservableCollection<AssignmentVM> Queue { get; } = [];

    private string _queueStatus = "";
    public string QueueStatus { get => _queueStatus; set => Set(ref _queueStatus, value); }

    public ICommand QueueCommand { get; }
    public ICommand SendCommand { get; }

    private void QueueAssignment()
    {
        var targets = new List<TargetRefVM>();
        foreach (var fact in Facts.Where(f => f.IsDraftTarget))
            targets.Add(new TargetRefVM
            { Kind = "event", Handle = fact.Handle, Label = fact.Label });
        foreach (var box in AllBoxes().Where(b => b.IsDraftTarget))
            targets.Add(new TargetRefVM
            { Kind = "person", Handle = box.Handle, Label = box.Name });

        NewEventVM? newEvent = null;
        if (DraftNewEvent)
        {
            if (s_familyEventTypes.Contains(EventType))
            {
                if (SelectedFamily is null)
                {
                    QueueStatus = $"{EventType} ist ein Familienereignis — "
                                  + "keine Familie vorhanden";
                    return;
                }
                newEvent = new NewEventVM
                {
                    EventType = EventType,
                    OwnerKind = "family",
                    OwnerHandle = SelectedFamily.Handle,
                    OwnerLabel = "Familie " + (SelectedFamily.Spouse?.PrimaryName
                                               ?? Center?.Name ?? ""),
                    Description = NullIfEmpty(EventDescription),
                };
            }
            else
            {
                if (Center is null)
                    return;
                newEvent = new NewEventVM
                {
                    EventType = EventType,
                    OwnerKind = "person",
                    OwnerHandle = Center.Handle,
                    OwnerLabel = Center.Name,
                    Description = NullIfEmpty(EventDescription),
                };
            }
        }

        if (targets.Count == 0 && newEvent is null)
        {
            QueueStatus = "keine Ziele markiert (📎 an Ereignis oder Person)";
            return;
        }

        Queue.Add(new AssignmentVM
        {
            RepoName = RepoName, RepoUrl = RepoUrl,
            SourceTitle = SourceTitle, SourceAuthor = SourceAuthor,
            SourceKey = SourceKey, CallNumber = CallNumber,
            Page = CitationPage, DateText = CitationDate,
            Confidence = Confidence, Permalink = Permalink,
            NoteText = NoteText, CopyLinkToPersons = CopyLinkToPersons,
            Targets = targets, NewEvent = newEvent,
        });

        foreach (var fact in Facts)
            fact.IsDraftTarget = false;
        foreach (var box in AllBoxes())
            box.IsDraftTarget = false;
        DraftNewEvent = false;
        RecomputeBadges();
        QueueStatus = $"{Queue.Count(a => a.Status == AssignmentStatus.Pending)} "
                      + "Zuordnung(en) ausstehend";
    }

    private CaptureRequest BuildRequest(AssignmentVM assignment)
    {
        var date = ParseDate(assignment.DateText);
        var request = new CaptureRequest
        {
            RequestId = Guid.NewGuid().ToString(),
            Repository = new RepositoryBlock
            {
                Match = new MatchSpec { By = "name", Value = assignment.RepoName },
                CreateIfMissing = new RepositoryCreate
                {
                    Name = assignment.RepoName,
                    Type = "Website",
                    Url = NullIfEmpty(assignment.RepoUrl),
                },
            },
            Source = new SourceBlock
            {
                Match = new MatchSpec
                {
                    By = "attribute", Key = "MH_SourceKey",
                    Value = assignment.SourceKey,
                },
                CreateIfMissing = new SourceCreate
                {
                    Title = assignment.SourceTitle,
                    Author = NullIfEmpty(assignment.SourceAuthor),
                    Attributes = [new AttributeKV("MH_SourceKey", assignment.SourceKey)],
                    RepositoryRef = new RepoRefSpec
                    {
                        CallNumber = NullIfEmpty(assignment.CallNumber),
                        MediaType = "Book",
                    },
                },
            },
            Citation = new CitationBlock
            {
                Page = NullIfEmpty(assignment.Page),
                Date = date,
                Confidence = assignment.Confidence,
                Attributes = string.IsNullOrWhiteSpace(assignment.Permalink)
                    ? null
                    : [new AttributeKV("MH_Permalink", assignment.Permalink)],
                Notes = string.IsNullOrWhiteSpace(assignment.NoteText)
                    ? null
                    : [new NoteSpec { Type = "Citation", Text = assignment.NoteText }],
            },
        };
        if (assignment.Targets.Count > 0)
            request.Targets = assignment.Targets
                .Select(t => new TargetRef { Type = t.Kind, Handle = t.Handle })
                .ToList();
        if (assignment.NewEvent is { } newEvent)
        {
            request.CreateEventIfMissing = new CreateEventBlock
            {
                PersonHandle = newEvent.OwnerKind == "person" ? newEvent.OwnerHandle : null,
                FamilyHandle = newEvent.OwnerKind == "family" ? newEvent.OwnerHandle : null,
                EventType = newEvent.EventType,
                Date = date,
                Description = newEvent.Description,
            };
        }
        if (assignment.CopyLinkToPersons
            && !string.IsNullOrWhiteSpace(assignment.Permalink))
        {
            request.PersonUrl = new PersonUrlSpec
            {
                Path = assignment.Permalink.Trim(),
                Description = assignment.NewEvent?.Label
                    ?? assignment.Targets.FirstOrDefault(t => t.Kind == "event")?.Label
                    ?? "Beleg " + assignment.Page,
                Type = "Digitalisat",
            };
        }
        return request;
    }

    private async Task SendAllAsync()
    {
        if (_client is null)
            return;
        var pending = Queue.Where(a => a.Status == AssignmentStatus.Pending).ToList();
        var succeeded = 0;
        foreach (var assignment in pending)
        {
            try
            {
                var request = BuildRequest(assignment);
                Log("capture request", request);
                var response = await _client.CaptureAsync(request);
                Log("capture response", response);
                assignment.CitationId = response.Created.Citation?.GrampsId;
                assignment.CreatedEventHandle = response.Created.Event?.Handle;
                assignment.Status = AssignmentStatus.Uploaded;
                succeeded++;
            }
            catch (BridgeException ex)
            {
                assignment.Error = $"{ex.Code}: {ex.Message}";
                assignment.Status = AssignmentStatus.Failed;
            }
            catch (Exception ex)
            {
                assignment.Error = ex.Message;
                assignment.Status = AssignmentStatus.Failed;
            }
        }

        // spec 7.3: read back the displayed slice, without moving the view
        if (Center is not null)
            await LoadCenterAsync(Center.Handle);
        RecomputeBadges();

        var failed = pending.Count - succeeded;
        QueueStatus = $"{succeeded} hochgeladen"
                      + (failed > 0 ? $", {failed} fehlgeschlagen" : "")
                      + " — Anzeige aus Gramps aktualisiert";
    }

    private void RecomputeBadges()
    {
        foreach (var fact in Facts)
        {
            fact.PendingCount = Queue.Count(a =>
                a.Status == AssignmentStatus.Pending
                && a.Targets.Any(t => t.Kind == "event" && t.Handle == fact.Handle));
            fact.UploadedCount = Queue.Count(a =>
                a.Status == AssignmentStatus.Uploaded
                && (a.Targets.Any(t => t.Kind == "event" && t.Handle == fact.Handle)
                    || a.CreatedEventHandle == fact.Handle));
        }
        foreach (var box in AllBoxes())
        {
            box.PendingCount = Queue.Count(a =>
                a.Status == AssignmentStatus.Pending
                && a.Targets.Any(t => t.Kind == "person" && t.Handle == box.Handle));
            box.UploadedCount = Queue.Count(a =>
                a.Status == AssignmentStatus.Uploaded
                && a.Targets.Any(t => t.Kind == "person" && t.Handle == box.Handle));
        }
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
