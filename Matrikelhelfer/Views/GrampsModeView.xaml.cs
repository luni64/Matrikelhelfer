using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using Matrikelhelfer.ViewModels;

namespace Matrikelhelfer.Views;

/// <summary>
/// Pure view logic of the Gramps-Modus tab: draws the Ancestry-style
/// connector lines between fact rows and source cards (which pairs is
/// the ViewModel's business, geometry and redraw scheduling are the
/// view's) and accepts tray cards dropped onto the sources column.
/// </summary>
partial class GrampsModeView : UserControl
{
    /// <summary>DataObject format for a tray card being dragged in.</summary>
    public const string FindingDataFormat = "MatrikelhelferFinding";

    bool _redrawQueued;
    GrampsViewModel? _viewModel;

    public GrampsModeView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (_viewModel is not null)
            {
                _viewModel.LinksChanged -= ScheduleRedraw;
            }
            _viewModel = DataContext as GrampsViewModel;
            if (_viewModel is not null)
            {
                _viewModel.LinksChanged += ScheduleRedraw;
            }
        };
        LinkArea.SizeChanged += (_, _) => ScheduleRedraw();
    }

    // ---- tray drop ---------------------------------------------------

    // The lists scroll INSIDE their panels now - item positions change
    // without a LinksChanged event, so scrolling must redraw the lines.
    void LinkList_ScrollChanged(object sender, ScrollChangedEventArgs e) =>
        ScheduleRedraw();

    // ---- children row paging (chevrons instead of a scrollbar) -------

    const double ChildPage = 110;   // one small box incl. margins

    void ChildLeft_Click(object sender, RoutedEventArgs e) =>
        ChildrenScroll.ScrollToHorizontalOffset(
            ChildrenScroll.HorizontalOffset - ChildPage);

    void ChildRight_Click(object sender, RoutedEventArgs e) =>
        ChildrenScroll.ScrollToHorizontalOffset(
            ChildrenScroll.HorizontalOffset + ChildPage);

    void ChildrenScroll_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        bool overflow = ChildrenScroll.ScrollableWidth > 0;
        ChildLeft.Visibility = ChildRight.Visibility =
            overflow ? Visibility.Visible : Visibility.Collapsed;
        ChildLeft.IsEnabled = ChildrenScroll.HorizontalOffset > 0;
        ChildRight.IsEnabled =
            ChildrenScroll.HorizontalOffset < ChildrenScroll.ScrollableWidth;
    }

    void Sources_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(FindingDataFormat)
            ? DragDropEffects.Link
            : DragDropEffects.None;
        e.Handled = true;
    }

    void Sources_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(FindingDataFormat) is SavedEntry entry)
        {
            _viewModel?.AdoptFinding(entry);
        }
        e.Handled = true;
    }

    // ---- connector lines ---------------------------------------------

    /// <summary>Coalesces redraw requests and defers them until after
    /// layout, so freshly generated item containers have positions.</summary>
    void ScheduleRedraw()
    {
        if (_redrawQueued)
        {
            return;
        }
        _redrawQueued = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            _redrawQueued = false;
            RedrawLinks();
        });
    }

    void RedrawLinks()
    {
        LinkCanvas.Children.Clear();
        if (_viewModel is null)
        {
            return;
        }
        var existingStroke = (Brush)FindResource("MahApps.Brushes.Gray4");
        var pendingStroke = (Brush)FindResource("MahApps.Brushes.Accent");
        foreach (var (fact, card, pending) in _viewModel.GetLinkPairs())
        {
            if (FactsList.ItemContainerGenerator.ContainerFromItem(fact)
                    is not FrameworkElement factElement
                || CardsList.ItemContainerGenerator.ContainerFromItem(card)
                    is not FrameworkElement cardElement)
            {
                continue;
            }

            var start = factElement.TranslatePoint(
                new Point(factElement.ActualWidth, factElement.ActualHeight / 2),
                LinkCanvas);
            var end = cardElement.TranslatePoint(
                new Point(0, cardElement.ActualHeight / 2), LinkCanvas);

            // horizontal S-curve through the gutter
            double reach = Math.Max(30, (end.X - start.X) / 2);
            var geometry = new StreamGeometry();
            using (var context = geometry.Open())
            {
                context.BeginFigure(start, isFilled: false, isClosed: false);
                context.BezierTo(new Point(start.X + reach, start.Y),
                                 new Point(end.X - reach, end.Y),
                                 end, isStroked: true, isSmoothJoin: false);
            }
            geometry.Freeze();

            LinkCanvas.Children.Add(new Path
            {
                Data = geometry,
                Stroke = pending ? pendingStroke : existingStroke,
                StrokeThickness = pending ? 1.8 : 1.5,
                StrokeDashArray = pending ? [4, 3] : null,
            });
        }
    }
}
