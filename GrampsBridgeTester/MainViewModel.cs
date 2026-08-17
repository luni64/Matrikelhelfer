using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Windows.Input;
using GrampsBridge;

namespace GrampsBridgeTester;

/// <summary>
/// Sandbox VM: everything the future MatrikelHelfer integration needs,
/// but with hand-typed values instead of scraped ones.
/// </summary>
public sealed class MainViewModel : INotifyPropertyChanged
{
    private BridgeClient? _client;
    private CaptureRequest? _lastRequest;

    public MainViewModel()
    {
        DiscoveryPath = Discovery.DefaultPath;
        ConnectCommand = new RelayCommand(async void () => await ConnectAsync());
        SearchCommand = new RelayCommand(async void () => await SearchAsync(),
                                         () => _client is not null);
        CaptureCommand = new RelayCommand(async void () => await CaptureAsync(),
                                          () => _client is not null);
        RepeatCaptureCommand = new RelayCommand(
            async void () => await RepeatCaptureAsync(),
            () => _client is not null && _lastRequest is not null);
        SourceKey = DeriveSourceKey(SourceTitle);
        Permalink = ComputePermalink();
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return false;
        field = value;
        OnChanged(name);
        return true;
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
                ConnectionStatus = "discovery file not found — is Gramps "
                                   + "running with the bridge gramplet?";
                _client = null;
                return;
            }
            if (!endpoint.IsProcessAlive())
            {
                ConnectionStatus = $"stale discovery file (pid {endpoint.Pid} "
                                   + "not running) — start Gramps";
                _client = null;
                return;
            }
            var client = new BridgeClient(endpoint);
            var ping = await client.PingAsync();
            _client = client;
            ConnectionStatus =
                $"connected: Gramps {ping.GrampsVersion}, addon {ping.AddonVersion}, "
                + (ping.TreeOpen ? $"tree \"{ping.TreeName}\"" : "NO TREE OPEN")
                + $", port {endpoint.Port}, session {ping.SessionId[..8]}…";
            Log("ping", ping);
        }
        catch (Exception ex)
        {
            _client = null;
            ConnectionStatus = "connect failed: " + ex.Message;
        }
    }

    // ---- person search ----------------------------------------------

    private string _searchSurname = "";
    public string SearchSurname { get => _searchSurname; set => Set(ref _searchSurname, value); }

    private string _searchGiven = "";
    public string SearchGiven { get => _searchGiven; set => Set(ref _searchGiven, value); }

    private string _searchQ = "";
    public string SearchQ { get => _searchQ; set => Set(ref _searchQ, value); }

    private string _searchBirthFrom = "";
    public string SearchBirthFrom { get => _searchBirthFrom; set => Set(ref _searchBirthFrom, value); }

    private string _searchBirthTo = "";
    public string SearchBirthTo { get => _searchBirthTo; set => Set(ref _searchBirthTo, value); }

    private string _searchPlace = "";
    public string SearchPlace { get => _searchPlace; set => Set(ref _searchPlace, value); }

    private string _searchStatus = "";
    public string SearchStatus { get => _searchStatus; set => Set(ref _searchStatus, value); }

    public ObservableCollection<PersonSummary> Results { get; } = [];

    private PersonSummary? _selectedPerson;
    public PersonSummary? SelectedPerson
    {
        get => _selectedPerson;
        set
        {
            if (Set(ref _selectedPerson, value) && value is not null)
                _ = LoadDetailAsync(value.Handle);
        }
    }

    public ICommand SearchCommand { get; }

    private async Task SearchAsync()
    {
        if (_client is null)
            return;
        try
        {
            var response = await _client.SearchPersonsAsync(
                q: NullIfEmpty(SearchQ),
                surname: NullIfEmpty(SearchSurname),
                given: NullIfEmpty(SearchGiven),
                birthYearFrom: ParseIntOrNull(SearchBirthFrom),
                birthYearTo: ParseIntOrNull(SearchBirthTo),
                place: NullIfEmpty(SearchPlace));
            Results.Clear();
            foreach (var person in response.Results)
                Results.Add(person);
            SearchStatus = $"{response.Total} match(es), showing {response.Results.Count}";
        }
        catch (Exception ex)
        {
            SearchStatus = "search failed: " + ex.Message;
        }
    }

    // ---- person detail ----------------------------------------------

    private string _detailHeader = "(no person selected)";
    public string DetailHeader { get => _detailHeader; set => Set(ref _detailHeader, value); }

    public ObservableCollection<PersonEvent> Events { get; } = [];

    private PersonEvent? _selectedEvent;
    public PersonEvent? SelectedEvent { get => _selectedEvent; set => Set(ref _selectedEvent, value); }

    public ObservableCollection<FamilyInfo> Families { get; } = [];

    private FamilyInfo? _selectedFamily;
    public FamilyInfo? SelectedFamily { get => _selectedFamily; set => Set(ref _selectedFamily, value); }

    private async Task LoadDetailAsync(string handle)
    {
        if (_client is null)
            return;
        try
        {
            var detail = await _client.GetPersonAsync(handle);
            DetailHeader = $"{detail.PrimaryName}  [{detail.GrampsId}]  "
                           + $"({detail.Gender}, {detail.CitationCount} person citations)";
            Events.Clear();
            foreach (var evt in detail.Events)
                Events.Add(evt);
            SelectedEvent = null;
            Families.Clear();
            foreach (var family in detail.Families)
                Families.Add(family);
            SelectedFamily = Families.FirstOrDefault();
            Log("person detail", detail);
        }
        catch (Exception ex)
        {
            DetailHeader = "detail failed: " + ex.Message;
        }
    }

    // ---- capture form ------------------------------------------------

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

    /// <summary>Mirrors the real app: the MH_SourceKey is derived from the
    /// book data, so key and title cannot drift apart. Uncheck to type a
    /// key manually (e.g. to test the mismatch warning).</summary>
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

    private string _sourceAuthor = "Kath. Pfarramt Testpfarrei";
    public string SourceAuthor { get => _sourceAuthor; set => Set(ref _sourceAuthor, value); }

    private string _sourcePublication = "";
    public string SourcePublication { get => _sourcePublication; set => Set(ref _sourcePublication, value); }

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

    /// <summary>Mirrors reality: every scan page has its own permalink.
    /// Derived from book key + page so distinct records never collide in
    /// the person-URL dedup. Uncheck to type a URL manually.</summary>
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

    private bool _copyLinkToPersons = true;
    public bool CopyLinkToPersons { get => _copyLinkToPersons; set => Set(ref _copyLinkToPersons, value); }

    private string _noteText = "Transkription: Joannes, ehel. Sohn des ...";
    public string NoteText { get => _noteText; set => Set(ref _noteText, value); }

    private bool _attachToSelectedEvent;
    public bool AttachToSelectedEvent { get => _attachToSelectedEvent; set => Set(ref _attachToSelectedEvent, value); }

    public string[] EventTypes { get; } =
        ["Baptism", "Christening", "Birth", "Marriage", "Death", "Burial"];

    /// <summary>Event types that live on the Family object in Gramps.</summary>
    private static readonly HashSet<string> s_familyEventTypes =
        ["Marriage", "Marriage Banns", "Engagement", "Divorce"];

    private string _eventType = "Baptism";
    public string EventType { get => _eventType; set => Set(ref _eventType, value); }

    private string _eventDescription = "Taufe laut Matrikel";
    public string EventDescription { get => _eventDescription; set => Set(ref _eventDescription, value); }

    private string _captureStatus = "";
    public string CaptureStatus { get => _captureStatus; set => Set(ref _captureStatus, value); }

    public ICommand CaptureCommand { get; }
    public ICommand RepeatCaptureCommand { get; }

    private CaptureRequest? BuildRequest()
    {
        if (AttachToSelectedEvent && SelectedEvent is null)
        {
            CaptureStatus = "no event selected to attach to";
            return null;
        }
        if (!AttachToSelectedEvent && SelectedPerson is null)
        {
            CaptureStatus = "no person selected for the new event";
            return null;
        }

        var date = ParseDate(CitationDate);
        var request = new CaptureRequest
        {
            RequestId = Guid.NewGuid().ToString(),
            Repository = new RepositoryBlock
            {
                Match = new MatchSpec { By = "name", Value = RepoName },
                CreateIfMissing = new RepositoryCreate
                {
                    Name = RepoName,
                    Type = "Website",
                    Url = NullIfEmpty(RepoUrl),
                },
            },
            Source = new SourceBlock
            {
                Match = new MatchSpec
                {
                    By = "attribute",
                    Key = "MH_SourceKey",
                    Value = SourceKey,
                },
                CreateIfMissing = new SourceCreate
                {
                    Title = SourceTitle,
                    Author = NullIfEmpty(SourceAuthor),
                    PublicationInfo = NullIfEmpty(SourcePublication),
                    Attributes = [new AttributeKV("MH_SourceKey", SourceKey)],
                    RepositoryRef = new RepoRefSpec
                    {
                        CallNumber = NullIfEmpty(CallNumber),
                        MediaType = "Book",
                    },
                },
            },
            Citation = new CitationBlock
            {
                Page = NullIfEmpty(CitationPage),
                Date = date,
                Confidence = Confidence,
                Attributes = string.IsNullOrWhiteSpace(Permalink)
                    ? null
                    : [new AttributeKV("MH_Permalink", Permalink)],
                Notes = string.IsNullOrWhiteSpace(NoteText)
                    ? null
                    : [new NoteSpec { Type = "Citation", Text = NoteText }],
            },
        };

        if (CopyLinkToPersons && !string.IsNullOrWhiteSpace(Permalink))
        {
            var eventLabel = AttachToSelectedEvent
                ? $"{SelectedEvent!.Type} {SelectedEvent.DateText}".Trim()
                : $"{EventType} {CitationDate}".Trim();
            request.PersonUrl = new PersonUrlSpec
            {
                Path = Permalink.Trim(),
                Description = eventLabel,
                Type = "Digitalisat",
            };
        }

        if (AttachToSelectedEvent)
        {
            request.Targets =
                [new TargetRef { Type = "event", Handle = SelectedEvent!.Handle }];
        }
        else if (s_familyEventTypes.Contains(EventType))
        {
            // family events (Marriage etc.) belong on the Family object
            if (SelectedFamily is null)
            {
                CaptureStatus = $"{EventType} is a family event — the selected "
                                + "person has no family to attach it to";
                return null;
            }
            request.CreateEventIfMissing = new CreateEventBlock
            {
                FamilyHandle = SelectedFamily.Handle,
                EventType = EventType,
                Date = date,
                Description = NullIfEmpty(EventDescription),
            };
        }
        else
        {
            request.CreateEventIfMissing = new CreateEventBlock
            {
                PersonHandle = SelectedPerson!.Handle,
                EventType = EventType,
                Role = "Primary",
                Date = date,
                Description = NullIfEmpty(EventDescription),
            };
        }
        return request;
    }

    private async Task CaptureAsync()
    {
        var request = BuildRequest();
        if (request is null)
            return;
        if (!await ConfirmSourceReuseAsync())
        {
            CaptureStatus = "cancelled";
            return;
        }
        await SendCaptureAsync(request);
    }

    /// <summary>
    /// Pre-flight (spec 7.2): if a source with this MH_SourceKey already
    /// exists, the capture will attach to IT and ignore the entered
    /// title. Warn when the titles disagree — that usually means the key
    /// was not updated after switching to a different book.
    /// </summary>
    private async Task<bool> ConfirmSourceReuseAsync()
    {
        if (_client is null || string.IsNullOrWhiteSpace(SourceKey))
            return true;
        try
        {
            var existing = await _client.SearchSourcesAsync(
                attributeKey: "MH_SourceKey", attributeValue: SourceKey);
            var hit = existing.Results.FirstOrDefault();
            if (hit is null || string.Equals(hit.Title?.Trim(), SourceTitle.Trim(),
                                             StringComparison.Ordinal))
                return true;
            var answer = System.Windows.MessageBox.Show(
                $"A source with key \"{SourceKey}\" already exists:\n\n"
                + $"    {hit.Title}  [{hit.GrampsId}]\n\n"
                + "The citation will be attached to that source; the title "
                + $"entered here (\"{SourceTitle}\") is ignored.\n\n"
                + "Different book? Press No and change the MH_SourceKey.\n\n"
                + "Attach to the existing source anyway?",
                "Source key already in use",
                System.Windows.MessageBoxButton.YesNo,
                System.Windows.MessageBoxImage.Warning);
            return answer == System.Windows.MessageBoxResult.Yes;
        }
        catch (Exception)
        {
            return true;    // pre-flight is advisory; the capture itself decides
        }
    }

    private async Task RepeatCaptureAsync()
    {
        if (_lastRequest is not null)
            await SendCaptureAsync(_lastRequest);   // same request_id -> idempotent
    }

    private async Task SendCaptureAsync(CaptureRequest request)
    {
        if (_client is null)
            return;
        try
        {
            CaptureStatus = "sending…";
            Log("capture request", request);
            var response = await _client.CaptureAsync(request);
            _lastRequest = request;
            Log("capture response", response);
            var source = response.Created.Source;
            var citation = response.Created.Citation;
            var sourceLabel = source?.Title is { } title
                ? $"\"{title}\" [{source.GrampsId}]"
                : source?.GrampsId;
            CaptureStatus =
                $"OK — citation {citation?.GrampsId} on source {sourceLabel}"
                + (source?.WasExisting == true ? " (reused)" : " (new)")
                + $", attached to {response.AttachedTo.Count} object(s)";
            if (response.Created.PersonUrls is { Count: > 0 } personUrls)
            {
                CaptureStatus += " — links: " + string.Join(", ",
                    personUrls.Select(u => (u.Name ?? u.GrampsId)
                        + (u.WasExisting ? " (already had it)" : " (new)")));
            }
        }
        catch (BridgeException ex)
        {
            CaptureStatus = $"bridge error {ex.Status} {ex.Code}: {ex.Message}";
            Log("capture error", new { ex.Status, ex.Code, ex.Message });
        }
        catch (Exception ex)
        {
            CaptureStatus = "capture failed: " + ex.Message;
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

    /// <summary>Slug in the spirit of the real app's CleanForKey:
    /// umlauts transliterated, accents stripped, non-alphanumeric runs
    /// collapsed to '-', lowercase.</summary>
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

    private static int? ParseIntOrNull(string value) =>
        int.TryParse(value, out var parsed) ? parsed : null;

    /// <summary>Accepts yyyy-MM-dd, yyyy-MM, or yyyy; empty -> null.</summary>
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
