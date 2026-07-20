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

    // The grid binds to these directly (Name, Info.CitationTitle,
    // Info.PageDescription, SavedAt, Comment as row tooltip).
    public MatriculaInfo Info => Page.Info;
    public string Name => Finding.Name;
    public string Comment => Finding.Comment;
    public DateTime SavedAt => Finding.SavedAt;

    // Same join without the view concerns - what the exporters take.
    public LibraryEntry Entry => new(Finding, Page);

    public event PropertyChangedEventHandler? PropertyChanged;

    public void Update(Finding finding, StoredPage page)
    {
        Finding = finding;
        Page = page;
        // Info covers the Info.* column bindings (CitationTitle,
        // PageDescription) - WPF re-reads the whole path when the root of it
        // changes.
        foreach (string property in new[] { nameof(Info), nameof(Name), nameof(Comment), nameof(SavedAt) })
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));
        }
    }
}
