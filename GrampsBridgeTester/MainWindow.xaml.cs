using System.Windows;
using System.Windows.Input;

namespace GrampsBridgeTester;

public partial class MainWindow : Window
{
    private const string FindDataFormat = "MatrikelHelferFind";

    private Point _dragStart;

    public MainWindow()
    {
        InitializeComponent();
    }

    // ---- drag source: the citation card ----------------------------

    private void CitationCard_PreviewMouseLeftButtonDown(object sender,
                                                         MouseButtonEventArgs e)
    {
        _dragStart = e.GetPosition(null);
    }

    private void CitationCard_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed)
            return;
        var position = e.GetPosition(null);
        if (Math.Abs(position.X - _dragStart.X) < SystemParameters.MinimumHorizontalDragDistance
            && Math.Abs(position.Y - _dragStart.Y) < SystemParameters.MinimumVerticalDragDistance)
            return;
        DragDrop.DoDragDrop((DependencyObject)sender,
                            new DataObject(FindDataFormat, "current-find"),
                            DragDropEffects.Link);
    }

    // ---- drop target: person boxes (template event handlers) --------

    private static PersonBoxVM? DropTargetBox(object sender) =>
        (sender as FrameworkElement)?.DataContext
            is PersonBoxVM { IsPlaceholder: false } box ? box : null;

    private void PersonBox_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(FindDataFormat)
                    && DropTargetBox(sender) is not null
            ? DragDropEffects.Link
            : DragDropEffects.None;
        e.Handled = true;
    }

    private void PersonBox_Drop(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(FindDataFormat))
            return;
        if (DropTargetBox(sender) is { } box && DataContext is MainViewModel vm)
            vm.DropFindOnPerson(box);
        e.Handled = true;
    }

    // ---- drop target: fact/event rows -------------------------------

    private static FactRowVM? DropTargetFact(object sender) =>
        (sender as FrameworkElement)?.DataContext as FactRowVM;

    private void FactRow_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(FindDataFormat)
                    && DropTargetFact(sender) is not null
            ? DragDropEffects.Link
            : DragDropEffects.None;
        e.Handled = true;
    }

    private void FactRow_Drop(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(FindDataFormat))
            return;
        if (DropTargetFact(sender) is { } fact && DataContext is MainViewModel vm)
            vm.DropFindOnFact(fact);
        e.Handled = true;
    }
}
