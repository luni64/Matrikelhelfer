using System;
using System.ComponentModel;
using System.Reflection;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Media.Animation;
using MahApps.Metro.Controls;
using Matrikelhelfer.ViewModels;
using MouseButtonEventArgs = System.Windows.Input.MouseButtonEventArgs;

namespace Matrikelhelfer.Views;

public partial class MainWindow : MetroWindow
{
    readonly MainViewModel _viewModel = new();
    bool _entriesPanelShown;

    // Width the docked saved-entries panel gets when opened; the window
    // widens/narrows by the same amount so the main fields never resize.
    // Mutable: the GridSplitter's drag result is remembered across toggles.
    double _entriesPanelWidth = 750;

    public MainWindow()
    {
        InitializeComponent();
        DataContext = _viewModel;
        _viewModel.PropertyChanged += OnViewModelPropertyChanged;

        // Single-sourced from <Version> in the csproj. InformationalVersion
        // keeps any pre-release suffix ("1.1.0-beta.1"), which the numeric
        // AssemblyVersion drops - important so a beta tester reports the exact
        // build. The SDK may append "+<gitsha>"; take the part before it.
        var informational = Assembly.GetExecutingAssembly()
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        string version = informational?.Split('+')[0]
            ?? Assembly.GetExecutingAssembly().GetName().Version?.ToString(3)
            ?? "?";
        Title = $"Matrikelhelfer v{version}";

        // Newest first by default; the SortDirection on the "Gespeichert"
        // column only draws the header arrow - the actual sort needs a
        // SortDescription. Header clicks replace it as usual.
        EntriesGrid.Items.SortDescriptions.Add(
            new SortDescription("SavedAt", ListSortDirection.Descending));
    }

    void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.IsEntriesPanelOpen))
        {
            UpdateEntriesPanel();
        }
    }

    // Docks/undocks the saved-entries panel. While OPEN, the roles of the
    // columns are inverted: the fields column is frozen at its current
    // pixel width and the panel column is the star-sized one - so the
    // toggle animation AND any manual window resizing only ever grow or
    // shrink the panel, never the fields. Closed, the fields column is
    // star-sized again and flexes with the window as usual. Pure view
    // geometry - the open/closed STATE lives in the ViewModel.
    void UpdateEntriesPanel()
    {
        if (_viewModel.IsEntriesPanelOpen == _entriesPanelShown)
        {
            return;
        }
        _entriesPanelShown = _viewModel.IsEntriesPanelOpen;

        // The splitter column materializes/vanishes together with the panel,
        // so its width must be part of the window delta on BOTH sides -
        // otherwise every open/close cycle leaks its 8px into the window.
        if (_entriesPanelShown)
        {
            ContentColumn.Width = new GridLength(ContentColumn.ActualWidth);
            EntriesColumn.Width = new GridLength(1, GridUnitType.Star);
            EntriesPanel.Visibility = Visibility.Visible;
            EntriesSplitter.Visibility = Visibility.Visible;
            // Hand the fields' right margin to the splitter column so the
            // whole gap between the panels is the drag handle.
            ContentGrid.Margin = new Thickness(15, 15, 0, 15);
            AnimateWindowWidth(Width + _entriesPanelWidth + EntriesSplitter.Width, onCompleted: null);
        }
        else
        {
            // Keep a splitter-adjusted width for the next open.
            _entriesPanelWidth = EntriesColumn.ActualWidth;
            AnimateWindowWidth(Width - _entriesPanelWidth - EntriesSplitter.ActualWidth, onCompleted: () =>
            {
                EntriesPanel.Visibility = Visibility.Collapsed;
                EntriesSplitter.Visibility = Visibility.Collapsed;
                EntriesColumn.Width = new GridLength(0);
                ContentColumn.Width = new GridLength(1, GridUnitType.Star);
                // Restore the symmetric margin now that there's no splitter.
                ContentGrid.Margin = new Thickness(15);
            });
        }
    }

    void AnimateWindowWidth(double to, Action? onCompleted)
    {
        var anim = new DoubleAnimation(Width, to, new Duration(TimeSpan.FromMilliseconds(220)))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };
        // The animation must be RELEASED on completion (BeginAnimation with
        // null) and the final value re-set locally - an animation holds its
        // property forever otherwise, which would block manual resizing.
        anim.Completed += (_, _) =>
        {
            BeginAnimation(WidthProperty, null);
            Width = to;
            onCompleted?.Invoke();
        };
        BeginAnimation(WidthProperty, anim);
    }

    // The splitter's drag pins BOTH neighbor columns to pixel widths -
    // restore the panel column's star sizing (it must stay the flexible
    // one while open) and remember the chosen width for the next toggle.
    void EntriesSplitter_DragCompleted(object sender, DragCompletedEventArgs e)
    {
        _entriesPanelWidth = EntriesColumn.ActualWidth;
        EntriesColumn.Width = new GridLength(1, GridUnitType.Star);
    }

    // Selection alone only redisplays the entry (see MainViewModel) -
    // double-click is the explicit "take my browser there".
    void EntriesGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        _viewModel.NavigateToSelectedEntry();
    }

    // A right-click doesn't select DataGrid rows on its own - without this,
    // the context menu would act on whatever row was selected BEFORE.
    void EntriesGrid_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is System.Windows.DependencyObject source &&
            System.Windows.Controls.ItemsControl.ContainerFromElement(EntriesGrid, source)
                is System.Windows.Controls.DataGridRow row)
        {
            row.IsSelected = true;
        }
    }
}
