using System.Diagnostics;
using System.Text.Json;

namespace GrampsBridge;

/// <summary>Content of the addon's discovery file (spec FA-3).</summary>
public sealed record GrampsEndpoint(
    int ApiVersion,
    int Port,
    string Token,
    int Pid,
    string? TreeName,
    string? GrampsVersion,
    string? AddonVersion,
    string? Started)
{
    public string BaseUrl => $"http://127.0.0.1:{Port}/api/v1";

    /// <summary>FA-C2: a discovery file whose pid is dead is stale.</summary>
    public bool IsProcessAlive()
    {
        try
        {
            return !Process.GetProcessById(Pid).HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }
}

public static class Discovery
{
    /// <summary>Default location on Windows (spec FA-C1).</summary>
    public static string DefaultPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                     "gramps", "matrikelhelfer", "endpoint.json");

    /// <summary>Reads the discovery file; null if it does not exist.</summary>
    public static GrampsEndpoint? Load(string? path = null)
    {
        path ??= DefaultPath;
        if (!File.Exists(path))
            return null;
        var json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<GrampsEndpoint>(json, BridgeJson.Options);
    }
}
