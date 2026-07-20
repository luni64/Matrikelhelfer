using System.Windows;
using MahApps.Metro.Controls;

namespace Matrikelhelfer.Views;

// Public to match the XAML-generated ChoiceWindow partial, which is public.
public enum ChoiceResult
{
    Primary,
    Secondary,
    Cancel
}

// A three-way question with real, self-describing buttons.
//
// Exists because the save flow asks genuine either/or questions ("overwrite
// this find, or add a second one?") that do NOT map onto OK/Cancel: forcing
// them into a MessageBox meant the buttons said "OK"/"Abbrechen" while the
// body text had to explain what each one would actually do - and "Abbrechen"
// then performed an action rather than cancelling, which is exactly what a
// Cancel button must never do.
public partial class ChoiceWindow : MetroWindow
{
    ChoiceResult _result = ChoiceResult.Cancel;

    ChoiceWindow(string title, string message, string primary, string secondary)
    {
        InitializeComponent();
        Title = title;
        MessageText.Text = message;
        PrimaryButton.Content = primary;
        SecondaryButton.Content = secondary;
    }

    // Primary is the accented default (Enter); Abbrechen is a true cancel
    // (Esc, window close) and never performs an action.
    public static ChoiceResult Ask(string title, string message, string primary, string secondary)
    {
        var owner = Application.Current?.MainWindow;
        var window = new ChoiceWindow(title, message, primary, secondary);
        if (owner is not null && !ReferenceEquals(owner, window) && owner.IsLoaded)
        {
            window.Owner = owner;
        }
        window.ShowDialog();
        return window._result;
    }

    void Primary_Click(object sender, RoutedEventArgs e) => Close(ChoiceResult.Primary);

    void Secondary_Click(object sender, RoutedEventArgs e) => Close(ChoiceResult.Secondary);

    void Cancel_Click(object sender, RoutedEventArgs e) => Close(ChoiceResult.Cancel);

    void Close(ChoiceResult result)
    {
        _result = result;
        DialogResult = result != ChoiceResult.Cancel;
    }
}
