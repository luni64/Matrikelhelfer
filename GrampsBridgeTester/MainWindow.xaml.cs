using System.Windows;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace GrampsBridgeTester;

/// <summary>
/// Pure view logic: draws the Ancestry-style connector lines between
/// fact rows and source cards. Which pairs to draw is the ViewModel's
/// business (GetLinkPairs); geometry, colors and redraw scheduling are
/// the view's.
/// </summary>
public partial class MainWindow : Window
{
    private static readonly Brush s_existingStroke =
        new SolidColorBrush(Color.FromRgb(0x8C, 0x8C, 0x8C));
    private static readonly Brush s_pendingStroke =
        new SolidColorBrush(Color.FromRgb(0x14, 0x40, 0xC8));

    private bool _redrawQueued;

    public MainWindow()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            if (DataContext is MainViewModel vm)
                vm.LinksChanged += ScheduleRedraw;
        };
        // size changes move rows/cards without a LinksChanged event
        LinkArea.SizeChanged += (_, _) => ScheduleRedraw();
    }

    /// <summary>Coalesces redraw requests and defers them until after
    /// layout, so freshly generated item containers have positions.</summary>
    private void ScheduleRedraw()
    {
        if (_redrawQueued)
            return;
        _redrawQueued = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            _redrawQueued = false;
            RedrawLinks();
        });
    }

    private void RedrawLinks()
    {
        LinkCanvas.Children.Clear();
        if (DataContext is not MainViewModel vm)
            return;
        foreach (var (fact, card, pending) in vm.GetLinkPairs())
        {
            if (FactsList.ItemContainerGenerator.ContainerFromItem(fact)
                    is not FrameworkElement factElement
                || CardsList.ItemContainerGenerator.ContainerFromItem(card)
                    is not FrameworkElement cardElement)
                continue;

            var start = factElement.TranslatePoint(
                new Point(factElement.ActualWidth, factElement.ActualHeight / 2),
                LinkCanvas);
            var end = cardElement.TranslatePoint(
                new Point(0, cardElement.ActualHeight / 2), LinkCanvas);

            // horizontal S-curve through the gutter
            var reach = Math.Max(30, (end.X - start.X) / 2);
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
                Stroke = pending ? s_pendingStroke : s_existingStroke,
                StrokeThickness = pending ? 1.8 : 1.5,
                StrokeDashArray = pending ? [4, 3] : null,
            });
        }
    }
}
