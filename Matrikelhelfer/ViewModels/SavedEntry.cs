using System;
using System.ComponentModel;
using Matrikelhelfer.Models;

namespace Matrikelhelfer.ViewModels;

// Joins a Finding to its StoredPage for the saved-entries DataGrid: the grid's
// columns bind to these accessors (and through Info to the computed display
// properties), while selection can recover both halves to redisplay and bind
// the entry for editing.
//
// Mutable + INotifyPropertyChanged rather than a fresh instance per edit: the
// grid keeps its selection and scroll position when a finding is updated in
// place, which is the normal case now that Speichern updates the bound entry
// instead of appending a new one.
class SavedEntry : INotifyPropertyChanged
{
    public Finding Finding { get; private set; }
    public StoredPage Page { get; private set; }

    public SavedEntry(Finding finding, StoredPage page)
    {
        Finding = finding;
        Page = page;
    }

    // The list binds to these directly (Comment doubles as the row tooltip).
    public MatriculaInfo Info => Page.Info;
    public string Comment => Finding.Comment;
    public DateTime SavedAt => Finding.SavedAt;

    // The shared card face: the tray cards and the Gramps tab's finding
    // cards render the same three lines from ONE definition, so the two
    // lists cannot drift apart visually.
    public string CardTitle =>
        Info.Pfarrei is { Length: > 0 } parish ? parish : Info.CitationTitle;
    public string CardSubtitle => Info.BookLabel;
    public string CardPage =>
        Info.Page ?? (Info.Scan > 0 ? "Scan " + Info.Scan : Info.ScanLabel);

    // First line of the comment as the card's lead line (the full text stays
    // the tooltip). With Name gone this is what tells two finds on the same
    // page apart.
    public string CardNote
    {
        get
        {
            int lineBreak = Comment.IndexOf('\n');
            return (lineBreak < 0 ? Comment : Comment[..lineBreak]).Trim();
        }
    }

    // Same join without the view concerns - what the exporters take.
    public LibraryEntry Entry => new(Finding, Page);

    public event PropertyChangedEventHandler? PropertyChanged;

    public void Update(Finding finding, StoredPage page)
    {
        Finding = finding;
        Page = page;
        // Info covers the Info.* column bindings (CitationTitle,
        // PageDescription) - WPF re-reads the whole path when the root of it
        // changes. The Card* accessors derive from Info/Comment but are bound
        // directly, so they need their own notifications.
        foreach (string property in new[] { nameof(Info), nameof(Comment), nameof(SavedAt),
                                            nameof(CardTitle), nameof(CardSubtitle), nameof(CardPage),
                                            nameof(CardNote) })
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));
        }
    }
}
