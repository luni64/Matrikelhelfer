using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Matrikelhelfer.Models;

namespace Matrikelhelfer.Services;

// Persists the saved finds as a flat JSON list (decision log: JSON over
// SQLite because entries accrue by manual clicks - thousands at most, far
// below JSON's pain threshold - and a readable file the user can back up
// beats a binary db for personal research data; if search/tags/10k+ entries
// ever materialize, swap this class's internals to SQLite plus a one-time
// import, the rest of the app only sees Load/Save).
//
// Unlike FormatSettingsStore (convenience state), a failed WRITE here is
// surfaced to the caller - these entries can represent years of research,
// so data loss must never be silent. Writes are atomic: serialize to a temp
// file, then swap it in, so a crash mid-write can't destroy the old file.
static class SavedEntryStore
{
    class Payload
    {
        public int Version { get; set; } = 1;
        public List<SavedRecord> Entries { get; set; } = new();
    }

    static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Matrikelhelfer", "entries.json");

    public static List<SavedRecord> Load()
    {
        try
        {
            var payload = JsonSerializer.Deserialize<Payload>(File.ReadAllText(FilePath));
            return payload?.Entries ?? new List<SavedRecord>();
        }
        catch (Exception)
        {
            // No file yet (first start) or unreadable JSON - start empty.
            return new List<SavedRecord>();
        }
    }

    // Throws on failure - the caller reports it to the user.
    public static void Save(IEnumerable<SavedRecord> entries)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        var payload = new Payload { Entries = new List<SavedRecord>(entries) };
        string json = JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true });

        string tmp = FilePath + ".tmp";
        File.WriteAllText(tmp, json);
        if (File.Exists(FilePath))
        {
            File.Replace(tmp, FilePath, destinationBackupFileName: null);
        }
        else
        {
            File.Move(tmp, FilePath);
        }
    }
}
