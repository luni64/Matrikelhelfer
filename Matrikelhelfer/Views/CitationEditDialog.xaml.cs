using System.Windows;
using MahApps.Metro.Controls;

namespace Matrikelhelfer.Views;

/// <summary>
/// Edits the citation-level fields of a saved find from the Gramps
/// tab's source cards: the handwritten page number (Seite — it cannot
/// be scraped) and the Kommentar (forwarded to Gramps as the citation
/// note on upload). The book/source fields are scrape-derived on
/// purpose and NOT editable here (see ARCHITECTURE.md, "Finds &amp;
/// pages": a field earns hand-editability only when it must).
/// </summary>
partial class CitationEditDialog : MetroWindow
{
    /// <summary>grampsCitation: true edits an EXISTING Gramps citation's
    /// page/Fundstelle (staged correction) — the note and the copy
    /// button are hidden then (notes are separate Gramps objects, and a
    /// "copy" only makes sense for the app's own finds).</summary>
    public CitationEditDialog(string context, string seite, string comment,
                              bool grampsCitation = false)
    {
        InitializeComponent();
        if (grampsCitation)
        {
            Title = "Gramps-Zitat korrigieren";
            SeiteLabel.Content = "Fundstelle/Seite:";
            SeiteBox.ToolTip = "Das Feld „Band/Seite“ des Gramps-Zitats.";
            NoteLabel.Visibility = System.Windows.Visibility.Collapsed;
            CommentBox.Visibility = System.Windows.Visibility.Collapsed;
            CopyButton.Visibility = System.Windows.Visibility.Collapsed;
        }
        ContextLine.Text = context;
        SeiteBox.Text = seite;
        CommentBox.Text = comment;
        SeiteBox.Focus();
        SeiteBox.CaretIndex = SeiteBox.Text.Length;
    }

    public string Seite => SeiteBox.Text;

    public string Comment => CommentBox.Text;

    /// <summary>True when "Als Kopie speichern" closed the dialog: the
    /// values go to a NEW finding on the same page; the original stays.</summary>
    public bool SaveAsCopy { get; private set; }

    void Save_Click(object sender, RoutedEventArgs e) =>
        DialogResult = true;

    void SaveAsCopy_Click(object sender, RoutedEventArgs e)
    {
        SaveAsCopy = true;
        DialogResult = true;
    }
}
