using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Web;

namespace GrampsBridge;

/// <summary>Error reported by the bridge (spec 5.1).</summary>
public sealed class BridgeException(int status, string code, string message)
    : Exception($"{code}: {message}")
{
    public int Status { get; } = status;
    public string Code { get; } = code;
}

/// <summary>
/// HTTP client for the MatrikelHelfer Bridge addon. One instance per
/// connection (base URL + token from the discovery file); stateless
/// otherwise, all calls async.
/// </summary>
public sealed class BridgeClient(GrampsEndpoint endpoint)
{
    private static readonly HttpClient s_http = new()
    {
        Timeout = TimeSpan.FromSeconds(60),
    };

    public GrampsEndpoint Endpoint { get; } = endpoint;

    // ---- endpoints ---------------------------------------------------

    public Task<PingResponse> PingAsync(CancellationToken ct = default) =>
        GetAsync<PingResponse>("/ping", ct);

    public Task<PersonSearchResponse> SearchPersonsAsync(
        string? q = null, string? surname = null, string? given = null,
        int? birthYearFrom = null, int? birthYearTo = null, string? place = null,
        int? limit = null, int? offset = null, CancellationToken ct = default)
    {
        var query = BuildQuery(
            ("q", q), ("surname", surname), ("given", given),
            ("birth_year_from", birthYearFrom?.ToString()),
            ("birth_year_to", birthYearTo?.ToString()),
            ("place", place),
            ("limit", limit?.ToString()), ("offset", offset?.ToString()));
        return GetAsync<PersonSearchResponse>("/persons" + query, ct);
    }

    public Task<PersonDetail> GetPersonAsync(string handle, CancellationToken ct = default) =>
        GetAsync<PersonDetail>("/persons/" + Uri.EscapeDataString(handle), ct);

    public Task<SourceSearchResponse> SearchSourcesAsync(
        string? q = null, string? attributeKey = null, string? attributeValue = null,
        CancellationToken ct = default)
    {
        var query = BuildQuery(("q", q), ("attribute_key", attributeKey),
                               ("attribute_value", attributeValue));
        return GetAsync<SourceSearchResponse>("/sources" + query, ct);
    }

    public Task<RepositorySearchResponse> SearchRepositoriesAsync(
        string? q = null, CancellationToken ct = default) =>
        GetAsync<RepositorySearchResponse>("/repositories" + BuildQuery(("q", q)), ct);

    /// <summary>The Gramps event editor's structured event-type catalog
    /// (groups + custom types of the open tree).</summary>
    public Task<EventTypeCatalog> GetEventTypesAsync(CancellationToken ct = default) =>
        GetAsync<EventTypeCatalog>("/event-types", ct);

    public Task<CaptureResponse> CaptureAsync(CaptureRequest request,
                                              CancellationToken ct = default) =>
        PostAsync<CaptureResponse>("/capture", request, ct);

    /// <summary>The whole Gramps-Modus upload as ONE transaction:
    /// persons, family links, events, citations, attaches —
    /// all-or-nothing, a single undo in Gramps.</summary>
    public Task<BatchResponse> CaptureBatchAsync(BatchRequest request,
                                                 CancellationToken ct = default) =>
        PostAsync<BatchResponse>("/capture-batch", request, ct);

    /// <summary>Attach an existing citation to further objects (5.8) —
    /// "this church record also evidences that fact".</summary>
    public Task<AttachResponse> AttachCitationAsync(
        string citationHandle, AttachRequest request,
        CancellationToken ct = default) =>
        PostAsync<AttachResponse>(
            "/citations/" + Uri.EscapeDataString(citationHandle) + "/attach",
            request, ct);

    // ---- plumbing ----------------------------------------------------

    private async Task<T> GetAsync<T>(string path, CancellationToken ct)
    {
        using var request = NewRequest(HttpMethod.Get, path);
        return await SendAsync<T>(request, ct).ConfigureAwait(false);
    }

    private async Task<T> PostAsync<T>(string path, object body, CancellationToken ct)
    {
        using var request = NewRequest(HttpMethod.Post, path);
        var json = JsonSerializer.Serialize(body, BridgeJson.Options);
        request.Content = new StringContent(json, Encoding.UTF8, "application/json");
        return await SendAsync<T>(request, ct).ConfigureAwait(false);
    }

    private HttpRequestMessage NewRequest(HttpMethod method, string path)
    {
        var request = new HttpRequestMessage(method, Endpoint.BaseUrl + path);
        request.Headers.Add("X-MatrikelHelfer-Token", Endpoint.Token);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return request;
    }

    private static async Task<T> SendAsync<T>(HttpRequestMessage request,
                                              CancellationToken ct)
    {
        using var response = await s_http.SendAsync(request, ct).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            BridgeErrorEnvelope? envelope = null;
            try
            {
                envelope = JsonSerializer.Deserialize<BridgeErrorEnvelope>(
                    body, BridgeJson.Options);
            }
            catch (JsonException)
            {
                // fall through to the generic error below
            }
            if (envelope?.Error is { } error)
                throw new BridgeException((int)response.StatusCode,
                                          error.Code, error.Message);
            throw new BridgeException((int)response.StatusCode, "HTTP_ERROR",
                                      $"HTTP {(int)response.StatusCode}");
        }
        return JsonSerializer.Deserialize<T>(body, BridgeJson.Options)
               ?? throw new BridgeException(500, "EMPTY_RESPONSE",
                                            "Response body was empty.");
    }

    private static string BuildQuery(params (string Name, string? Value)[] parameters)
    {
        var parts = parameters
            .Where(p => !string.IsNullOrWhiteSpace(p.Value))
            .Select(p => $"{p.Name}={HttpUtility.UrlEncode(p.Value)}")
            .ToList();
        return parts.Count == 0 ? "" : "?" + string.Join("&", parts);
    }
}
