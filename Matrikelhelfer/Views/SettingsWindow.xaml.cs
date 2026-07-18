using System.Windows;
using System.Windows.Controls;
using MahApps.Metro.Controls;
using Matrikelhelfer.Services;
using Matrikelhelfer.ViewModels;

namespace Matrikelhelfer.Views;

// Editor for the source- and citation-format templates: a dropdown switches
// the visible target (Quellen-/Zitatformate), below it that target's format
// list, and a shared editor pane (name/template fields + clickable
// placeholder chips + live preview). OK/Cancel semantics: the ViewModel
// edits a copy, and the caller reads Result back only after ShowDialog()
// returned true. Code-behind holds only pure view logic (caret-position
// insertion, dialog result).
public partial class SettingsWindow : MetroWindow
{
    readonly FormatEditorViewModel _viewModel;

    internal FormatSettings Result => _viewModel.ToSettings();

    internal SettingsWindow(FormatSettings settings)
    {
        InitializeComponent();
        _viewModel = new FormatEditorViewModel(settings);
        DataContext = _viewModel;
    }

    // The chips are Focusable=False, so the TextBox keeps focus and caret -
    // insert the clicked token exactly where the user was typing. The chips
    // show the bare name; the braces are added here on insertion.
    void Placeholder_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Content: string name })
        {
            return;
        }
        string token = $"{{{name}}}";
        int caret = TemplateBox.CaretIndex;
        TemplateBox.Text = TemplateBox.Text.Insert(caret, token);
        TemplateBox.CaretIndex = caret + token.Length;
        TemplateBox.Focus();
    }

    void Ok_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
    }
}
