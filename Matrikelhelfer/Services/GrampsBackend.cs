using System;
using System.Threading.Tasks;
using GrampsBridge;

namespace Matrikelhelfer.Services;

// The single seam between the app and the Gramps bridge: EVERY backend
// call goes through this adapter, never through BridgeClient directly
// (multi-backend outlook, spec §1 - when a second genealogy backend
// becomes concrete, the interface gets extracted from this one class).
//
// Stage 2 scope: discovery + connect + ping + connection state
// (FA-C1..C4). The read/write calls follow with the Gramps-Modus view.
class GrampsBackend
{
    // The api version this client speaks; a mismatching bridge is
    // reported instead of silently misbehaving (FA-C3).
    const int SupportedApiVersion = 1;

    BridgeClient? _client;

    public bool IsConnected => _client is not null;

    public bool TreeOpen { get; private set; }
    public string? TreeName { get; private set; }
    public string? GrampsVersion { get; private set; }
    public string? AddonVersion { get; private set; }

    // Changes whenever Gramps switches trees - cached handles must be
    // dropped then (FA-C4; consumed by the Gramps-Modus view later).
    public string? SessionId { get; private set; }

    /// <summary>Discovery file -> liveness check -> ping. Returns a
    /// user-displayable status either way; a failure leaves the adapter
    /// disconnected.</summary>
    public async Task<GrampsConnectResult> ConnectAsync()
    {
        Disconnect();

        GrampsEndpoint? endpoint;
        try
        {
            endpoint = Discovery.Load(null);
        }
        catch (Exception ex)
        {
            return GrampsConnectResult.Failure(
                $"Verbindungsdatei nicht lesbar: {ex.Message}");
        }
        if (endpoint is null)
        {
            return GrampsConnectResult.Failure(
                "Gramps ist nicht erreichbar – bitte Gramps (mit dem " +
                "MatrikelHelfer-Bridge-Addon) starten.");
        }
        if (!endpoint.IsProcessAlive())
        {
            return GrampsConnectResult.Failure(
                "Gramps läuft nicht mehr (veraltete Verbindungsdatei) – " +
                "bitte Gramps neu starten.");
        }

        var client = new BridgeClient(endpoint);
        PingResponse ping;
        try
        {
            ping = await client.PingAsync();
        }
        catch (Exception ex)
        {
            var message = ex is BridgeException bridge
                ? $"{bridge.Code}: {bridge.Message}" : ex.Message;
            return GrampsConnectResult.Failure(
                $"Bridge antwortet nicht: {message}");
        }
        if (ping.ApiVersion != SupportedApiVersion)
        {
            return GrampsConnectResult.Failure(
                $"Bridge-Version passt nicht (API {ping.ApiVersion}, " +
                $"erwartet {SupportedApiVersion}) – bitte Addon bzw. " +
                "MatrikelHelfer aktualisieren.");
        }

        _client = client;
        TreeOpen = ping.TreeOpen;
        TreeName = ping.TreeName;
        GrampsVersion = ping.GrampsVersion;
        AddonVersion = ping.AddonVersion;
        SessionId = ping.SessionId;

        string status = ping.TreeOpen
            ? $"Verbunden: Gramps {ping.GrampsVersion}, Stammbaum „{ping.TreeName}“"
            : $"Verbunden: Gramps {ping.GrampsVersion} – es ist KEIN Stammbaum geöffnet.";
        return GrampsConnectResult.Ok(status, ping.TreeOpen);
    }

    public void Disconnect()
    {
        _client = null;
        TreeOpen = false;
        TreeName = null;
        GrampsVersion = null;
        AddonVersion = null;
        SessionId = null;
    }
}

class GrampsConnectResult
{
    public bool Success { get; private init; }
    public bool TreeOpen { get; private init; }
    public string Message { get; private init; } = "";

    public static GrampsConnectResult Ok(string message, bool treeOpen) =>
        new() { Success = true, TreeOpen = treeOpen, Message = message };

    public static GrampsConnectResult Failure(string message) =>
        new() { Success = false, Message = message };
}
