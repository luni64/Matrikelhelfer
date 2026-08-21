using System.Text.Json;
using System.Text.Json.Serialization;

namespace GrampsBridge;

/// <summary>Shared serializer settings: the bridge API speaks snake_case.</summary>
public static class BridgeJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true,
    };
}

// ---- /ping (5.3) ----------------------------------------------------

public sealed record PingResponse(
    int ApiVersion, string? AddonVersion, string? GrampsVersion,
    bool TreeOpen, string? TreeName, string? TreeId, string SessionId);

// ---- /persons (5.4 / 5.5) ------------------------------------------

public sealed record LifeEvent(string? DateText, int? SortYear, string? Place);

public sealed record PersonBrief(
    string Handle, string? GrampsId, string? PrimaryName,
    string? Gender = null, LifeEvent? Birth = null, LifeEvent? Death = null);

public sealed record PersonSummary(
    string Handle, string GrampsId, string PrimaryName,
    string Surname, string Given, string? CallName, string Gender,
    LifeEvent? Birth, LifeEvent? Death, List<PersonBrief> Parents)
{
    [JsonIgnore]
    public string ParentsDisplay =>
        string.Join(", ", Parents.Select(p => p.PrimaryName));
}

public sealed record PersonSearchResponse(int Total, List<PersonSummary> Results);

public sealed record NameInfo(
    string Type, bool Primary, string? Given, string? CallName,
    string? Surname, string? Display);

/// <summary>One citation on an event/person, incl. its source — feeds
/// the Gramps-Modus link view (source cards + connector lines).</summary>
public sealed record CitationRef(
    string Handle, string SourceHandle, string? SourceTitle,
    string? SourceAbbrev, string? Page, string? DateText,
    long? Change = null,           // staleness guard for updates
    string? Url = null)            // "Digitalisat" attribute (scan link)
{
    /// <summary>Compact card label: abbreviation when maintained,
    /// else the full title (hand-made Gramps sources often have none).</summary>
    [JsonIgnore]
    public string SourceLabel =>
        !string.IsNullOrWhiteSpace(SourceAbbrev) ? SourceAbbrev
        : SourceTitle ?? "(Quelle ohne Titel)";
}

/// <summary>Scope is "person" or "family" — family events (Marriage etc.)
/// live on the Family object in Gramps, FamilyHandle says which one.</summary>
public sealed record PersonEvent(
    string Handle, string GrampsId, string Type, string Role,
    string? DateText, int? SortYear, string? Place, string? Description,
    int CitationCount, string? Scope = null, string? FamilyHandle = null,
    List<CitationRef>? Citations = null,
    long? Change = null);          // staleness guard for updates/deletes

public sealed record FamilyInfo(
    string Handle, string GrampsId, PersonBrief? Spouse, List<PersonBrief> Children)
{
    [JsonIgnore]
    public string Display =>
        (Spouse?.PrimaryName is { } name ? $"with {name}" : "(no spouse recorded)")
        + $", {Children.Count} child(ren)";
}

public sealed record PersonDetail(
    string Handle, string GrampsId, string PrimaryName, string Gender,
    int CitationCount, List<NameInfo> Names, List<PersonEvent> Events,
    List<PersonBrief> Parents, List<FamilyInfo> Families,
    LifeEvent? Birth = null, LifeEvent? Death = null,
    List<CitationRef>? Citations = null,
    string? ParentFamilyHandle = null,
    long? Change = null);          // staleness guard for updates

// ---- /sources, /repositories (5.6) ----------------------------------

public sealed record AttributeKV(string Key, string Value);

public sealed record SourceRepositoryRef(
    string Handle, string? Name, string? CallNumber, string? MediaType);

public sealed record SourceHit(
    string Handle, string GrampsId, string? Title, string? Author,
    string? PublicationInfo, string? Abbreviation,
    List<AttributeKV> Attributes, List<SourceRepositoryRef> Repositories);

public sealed record SourceSearchResponse(int Total, List<SourceHit> Results);

public sealed record RepositoryUrl(string? Path, string? Description, string? Type);

public sealed record RepositoryHit(
    string Handle, string GrampsId, string? Name, string? Type,
    List<RepositoryUrl> Urls);

public sealed record RepositorySearchResponse(int Total, List<RepositoryHit> Results);

// ---- GET /event-types ------------------------------------------------

/// <summary>Xml is the locale-independent name the capture endpoint
/// consumes (set_from_xml_str); Label is what the user sees. IsFamily
/// mirrors the Gramps "Family" menu group — those events belong on the
/// Family object, not the person.</summary>
public sealed record EventTypeInfo(string Xml, string Label, bool IsFamily);

public sealed record EventTypeGroup(string Name, List<EventTypeInfo> Types);

/// <summary>The Gramps event editor's structured type catalog plus the
/// custom types already used in the open tree.</summary>
public sealed record EventTypeCatalog(
    List<EventTypeGroup> Groups, List<string> Custom);

// ---- POST /capture request (5.7 / 5.9) -------------------------------

public sealed class DateSpec
{
    public string Type { get; set; } = "regular";
    public int? Year { get; set; }
    public int? Month { get; set; }
    public int? Day { get; set; }
    public int? YearEnd { get; set; }
    public int? MonthEnd { get; set; }
    public int? DayEnd { get; set; }
    public string? Calendar { get; set; }
    public string? Quality { get; set; }
    public string? Text { get; set; }
}

public sealed class MatchSpec
{
    public required string By { get; set; }     // name | title | attribute | handle
    public string? Value { get; set; }
    public string? Key { get; set; }            // for by=attribute
}

public sealed class RepositoryCreate
{
    public required string Name { get; set; }
    public string? Type { get; set; }
    public string? Url { get; set; }
}

public sealed class RepositoryBlock
{
    public MatchSpec? Match { get; set; }
    public RepositoryCreate? CreateIfMissing { get; set; }
}

public sealed class RepoRefSpec
{
    public string? CallNumber { get; set; }
    public string? MediaType { get; set; }
}

public sealed class SourceCreate
{
    public required string Title { get; set; }
    public string? Author { get; set; }
    public string? PublicationInfo { get; set; }
    public string? Abbreviation { get; set; }
    public List<AttributeKV>? Attributes { get; set; }
    public RepoRefSpec? RepositoryRef { get; set; }
}

public sealed class SourceBlock
{
    public MatchSpec? Match { get; set; }
    public SourceCreate? CreateIfMissing { get; set; }
}

public sealed class NoteSpec
{
    public string? Type { get; set; }
    public required string Text { get; set; }
}

public sealed class CitationBlock
{
    public string? Page { get; set; }
    public DateSpec? Date { get; set; }
    public string? Confidence { get; set; }
    public List<AttributeKV>? Attributes { get; set; }
    public List<NoteSpec>? Notes { get; set; }
}

public sealed class TargetRef
{
    public required string Type { get; set; }   // person | event | family
    public required string Handle { get; set; }
}

public sealed class CreateEventBlock
{
    // exactly one of PersonHandle / FamilyHandle (family events like
    // Marriage belong on the family)
    public string? PersonHandle { get; set; }
    public string? FamilyHandle { get; set; }
    public required string EventType { get; set; }
    public string? Role { get; set; }
    public DateSpec? Date { get; set; }
    public string? PlaceHandle { get; set; }
    public PlaceSpec? Place { get; set; }           // by name (match or create)
    public string? Description { get; set; }
}

/// <summary>Event place by NAME: the bridge reuses an existing place
/// whose name matches (casefolded), else creates a bare place with
/// this title in the same transaction.</summary>
public sealed class PlaceSpec
{
    public required string Title { get; set; }
}

/// <summary>Mirrors the permalink onto the Internet tab of the involved
/// persons (family events: both partners). Deduplicated by path.</summary>
public sealed class PersonUrlSpec
{
    public required string Path { get; set; }
    public string? Description { get; set; }
    public string? Type { get; set; }           // default "Digitalisat"
}

/// <summary>Create a person and link them into the tree (spec 7.3 v2).
/// Exactly one of ChildOfFamily / ChildOfPerson / SpouseOf / ParentOf.
/// SpouseOf may add FamilyHandle to join an existing partner-less
/// family instead of creating a new one. In the same capture,
/// CreateEventBlock may reference the new person/family as "@new".</summary>
public sealed class CreatePersonBlock
{
    public string? Given { get; set; }
    public string? Surname { get; set; }
    public string? Gender { get; set; }         // M | F | U
    public string? ChildOfFamily { get; set; }
    public string? ChildOfPerson { get; set; }
    public string? SpouseOf { get; set; }
    public string? FamilyHandle { get; set; }
    public string? ParentOf { get; set; }
}

/// <summary>Source and Citation may be omitted only for a bare
/// create_person/create_event capture (nothing to attach then).</summary>
public sealed class CaptureRequest
{
    public string? RequestId { get; set; }
    public RepositoryBlock? Repository { get; set; }
    public SourceBlock? Source { get; set; }
    public CitationBlock? Citation { get; set; }
    public List<TargetRef>? Targets { get; set; }
    public CreatePersonBlock? CreatePerson { get; set; }
    public CreateEventBlock? CreateEventIfMissing { get; set; }
    public PersonUrlSpec? PersonUrl { get; set; }
}

// ---- POST /capture response ------------------------------------------

/// <summary>Title is only set for sources (shows which source was
/// matched/created, since an attribute match ignores the entered title).</summary>
public sealed record CreatedObject(string Handle, string GrampsId, bool WasExisting,
                                   string? Title = null);

public sealed record CreatedNote(string Handle, string GrampsId);

public sealed record PersonUrlResult(
    string Handle, string GrampsId, string? Name, bool WasExisting);

public sealed record CreatedInfo(
    CreatedObject? Repository, CreatedObject? Source, CreatedObject? Citation,
    CreatedObject? Event, List<CreatedNote>? Notes,
    List<PersonUrlResult>? PersonUrls = null,
    CreatedObject? Person = null, CreatedObject? Family = null);

public sealed record AttachedTo(string Type, string Handle, string? GrampsId);

public sealed record CaptureResponse(
    string? RequestId, CreatedInfo Created, List<AttachedTo> AttachedTo,
    string TransactionLabel);

// ---- POST /capture-batch ---------------------------------------------
//
// The whole Gramps-Modus upload in ONE transaction. References are
// either real Gramps handles or temp ids the client chooses (any
// string); the addon resolves persons -> families -> events ->
// citations in order, all-or-nothing, one undo.

public sealed class BatchPersonSpec
{
    public required string Tmp { get; set; }
    public string? Given { get; set; }
    public string? Surname { get; set; }
    public string? Gender { get; set; }
}

/// <summary>New family (Tmp) or ADD members to an existing one
/// (Handle). Occupied partner slots are a 400 — members are only ever
/// added, never replaced.</summary>
public sealed class BatchFamilySpec
{
    public string? Tmp { get; set; }
    public string? Handle { get; set; }
    public string? Father { get; set; }
    public string? Mother { get; set; }
    public List<string>? Children { get; set; }
}

public sealed class BatchEventSpec
{
    public required string Tmp { get; set; }
    public required string Type { get; set; }       // event-type xml name
    public string? Person { get; set; }             // exactly one of these
    public string? Family { get; set; }
    public string? Role { get; set; }
    public DateSpec? Date { get; set; }
    public PlaceSpec? Place { get; set; }           // by name (match or create)
    public string? Description { get; set; }
}

public sealed class BatchTargetRef
{
    public required string Type { get; set; }       // person | event | family
    public required string Ref { get; set; }        // handle | temp id
}

public sealed class BatchCitationSpec
{
    public RepositoryBlock? Repository { get; set; }
    public SourceBlock? Source { get; set; }
    public required CitationBlock Citation { get; set; }
    public required List<BatchTargetRef> Targets { get; set; }
    public PersonUrlSpec? PersonUrl { get; set; }
}

public sealed class BatchAttachSpec
{
    public required string Citation { get; set; }   // existing citation handle
    public required List<BatchTargetRef> Targets { get; set; }
}

/// <summary>Sparse update of an EXISTING Gramps object (persons:
/// given/surname/gender; events: type/date/place/description). Only
/// keys present in Set change - a null value clears the field.
/// ExpectChange = the object's change time as read; the bridge aborts
/// the whole batch with 409 CONFLICT when it moved since.</summary>
public sealed class BatchUpdateSpec
{
    public required string Type { get; set; }       // "person" | "event"
    public required string Handle { get; set; }
    public long? ExpectChange { get; set; }
    public required Dictionary<string, object?> Set { get; set; }
}

/// <summary>Delete of an EXISTING Gramps event (the only deletable
/// type by design); the bridge removes all event_refs first.</summary>
public sealed class BatchDeleteSpec
{
    public required string Type { get; set; }       // "event"
    public required string Handle { get; set; }
    public long? ExpectChange { get; set; }
}

public sealed class BatchRequest
{
    public string? RequestId { get; set; }
    public List<BatchPersonSpec> Persons { get; set; } = [];
    public List<BatchFamilySpec> Families { get; set; } = [];
    public List<BatchEventSpec> Events { get; set; } = [];
    public List<BatchCitationSpec> Citations { get; set; } = [];
    public List<BatchAttachSpec> Attach { get; set; } = [];
    public List<BatchUpdateSpec> Updates { get; set; } = [];
    public List<BatchDeleteSpec> Deletes { get; set; } = [];
}

public sealed record BatchCitationResult(
    string Handle, string GrampsId, CreatedObject? Repository,
    CreatedObject? Source, List<CreatedNote>? Notes,
    List<PersonUrlResult>? PersonUrls = null);

public sealed record BatchAttachResult(
    string Citation, List<AttachedObject> AttachedTo);

public sealed record BatchCreated(
    Dictionary<string, CreatedObject> Persons,
    Dictionary<string, CreatedObject> Families,
    Dictionary<string, CreatedObject> Events,
    List<BatchCitationResult> Citations,
    List<BatchAttachResult> Attaches);

public sealed record BatchResponse(
    string? RequestId, BatchCreated Created, string TransactionLabel);

// ---- POST /citations/{handle}/attach (5.8) ---------------------------

public sealed class AttachRequest
{
    public string? RequestId { get; set; }
    public required List<TargetRef> Targets { get; set; }
}

public sealed record AttachedObject(
    string Type, string Handle, string? GrampsId, bool WasExisting);

public sealed record AttachResponse(
    string? RequestId, CreatedNote Citation, List<AttachedObject> AttachedTo,
    string TransactionLabel);

// ---- error envelope (5.1) --------------------------------------------

public sealed record BridgeErrorBody(string Code, string Message, string? Detail);

public sealed record BridgeErrorEnvelope(BridgeErrorBody Error);
