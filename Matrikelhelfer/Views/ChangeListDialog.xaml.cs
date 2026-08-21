using MahApps.Metro.Controls;
using Matrikelhelfer.ViewModels;

namespace Matrikelhelfer.Views;

/// <summary>
/// The Änderungsliste in its own (resizable) window — the Gramps tab
/// only shows a summary row. Shares the GrampsViewModel as DataContext,
/// so deleting entries and sending the batch work identically here and
/// the list stays live while the dialog is open.
/// </summary>
partial class ChangeListDialog : MetroWindow
{
    public ChangeListDialog(GrampsViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }
}
