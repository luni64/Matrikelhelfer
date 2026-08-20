using System;

namespace Matrikelhelfer.Models;

// A Finding joined to its StoredPage - the flat shape the exporters consume.
//
// Exists so Services keep depending on Models only: the ViewModels' SavedEntry
// is the same join, but it carries view concerns (INotifyPropertyChanged, so
// the grid updates in place) that an exporter has no business seeing.
record LibraryEntry(Finding Finding, StoredPage Page)
{
    public MatriculaInfo Info => Page.Info;
    public string Comment => Finding.Comment;
    public DateTime SavedAt => Finding.SavedAt;
}
