using System.Windows;
using MahApps.Metro.Controls;

namespace Matrikelhelfer.Views;

/// <summary>
/// Name/gender entry for a virtual person created from a "Neu" tree
/// slot. Gender and surname arrive prefilled from the slot (a father
/// slot is male and usually carries the child's surname); everything
/// stays editable. Anlegen needs at least one name part.
/// </summary>
partial class NewPersonDialog : MetroWindow
{
    public NewPersonDialog(string context, string given, string surname,
                           string gender)
    {
        InitializeComponent();
        ContextLine.Text = context;
        GivenBox.Text = given;
        SurnameBox.Text = surname;
        (gender switch
        {
            "M" => MaleRadio,
            "F" => FemaleRadio,
            _ => UnknownRadio,
        }).IsChecked = true;
        GivenBox.Focus();
        GivenBox.CaretIndex = GivenBox.Text.Length;
    }

    public string Given => GivenBox.Text.Trim();

    public string Surname => SurnameBox.Text.Trim();

    public string Gender =>
        MaleRadio.IsChecked == true ? "M"
        : FemaleRadio.IsChecked == true ? "F" : "U";

    void Name_TextChanged(object sender, RoutedEventArgs e) =>
        SaveButton.IsEnabled = Given.Length > 0 || Surname.Length > 0;

    void Save_Click(object sender, RoutedEventArgs e) =>
        DialogResult = true;
}
