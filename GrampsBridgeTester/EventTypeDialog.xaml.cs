using System.Windows;
using System.Windows.Data;
using System.Windows.Input;

namespace GrampsBridgeTester;

/// <summary>
/// Modal event-type picker for "+ Ereignis vormerken": the grouped
/// Gramps catalog as a full list (a ComboBox proved too clumsy for
/// ~45 grouped entries) plus the optional description, with real
/// Save/Cancel semantics. Double-click on a type saves directly.
/// </summary>
public partial class EventTypeDialog : Window
{
    public EventTypeDialog(IEnumerable<EventTypeChoice> choices,
                           EventTypeChoice? preselect)
    {
        InitializeComponent();
        var view = new ListCollectionView(choices.ToList());
        view.GroupDescriptions!.Add(
            new PropertyGroupDescription(nameof(EventTypeChoice.Group)));
        TypeList.ItemsSource = view;
        if (preselect is not null)
        {
            TypeList.SelectedItem = preselect;
            TypeList.ScrollIntoView(preselect);
        }
    }

    public EventTypeChoice? SelectedType => TypeList.SelectedItem as EventTypeChoice;

    public string Description => DescriptionBox.Text;

    private void TypeList_SelectionChanged(object sender, RoutedEventArgs e) =>
        SaveButton.IsEnabled = SelectedType is not null;

    private void Save_Click(object sender, RoutedEventArgs e) =>
        DialogResult = true;

    private void TypeList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (SelectedType is not null)
            DialogResult = true;
    }
}
