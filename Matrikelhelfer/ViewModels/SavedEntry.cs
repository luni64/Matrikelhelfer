using Matrikelhelfer.Models;

namespace Matrikelhelfer.ViewModels;

// Wraps a persisted SavedRecord for the saved-entries DataGrid: the grid's
// columns bind to these accessors (and through Info/Record to the computed
// display properties), while selection can still recover the full record to
// redisplay its details.
class SavedEntry
{
    public SavedRecord Record { get; }

    public MatriculaInfo Info => Record.Info;
    public string Name => Record.Name;
    public string Comment => Record.Comment;

    public SavedEntry(SavedRecord record)
    {
        Record = record;
    }
}
