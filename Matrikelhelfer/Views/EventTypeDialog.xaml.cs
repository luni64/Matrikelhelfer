using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Data;
using System.Windows.Input;
using MahApps.Metro.Controls;
using Matrikelhelfer.ViewModels;

namespace Matrikelhelfer.Views;

/// <summary>
/// Modal event-type picker for "+ Ereignis vormerken": the grouped
/// Gramps catalog as a full list, the EVENT's own date (freely editable
/// incl. Gramps qualifiers — a marriage entry's "Mutter bereits
/// verstorben" becomes a death "vor 1757", not a death on the wedding
/// day) and the optional description. Double-click on a type saves.
/// </summary>
partial class EventTypeDialog : MetroWindow
{
    static readonly (string Label, string Type)[] s_dateModifiers =
    [
        ("genau", "regular"),
        ("um", "about"),
        ("vor", "before"),
        ("nach", "after"),
    ];

    public EventTypeDialog(IEnumerable<EventTypeChoice> choices,
                           EventTypeChoice? preselect, string dateText)
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
        DateModifierBox.ItemsSource = s_dateModifiers.Select(m => m.Label);
        DateModifierBox.SelectedIndex = 0;
        DateBox.Text = dateText;
    }

    public EventTypeChoice? SelectedType => TypeList.SelectedItem as EventTypeChoice;

    public string Description => DescriptionBox.Text;

    public string DateText => DateBox.Text.Trim();

    /// <summary>DateSpec type value: regular | about | before | after.</summary>
    public string DateType =>
        s_dateModifiers[Math.Max(0, DateModifierBox.SelectedIndex)].Type;

    /// <summary>Display form incl. qualifier ("vor 1757"); empty = no date.</summary>
    public string DateDisplay =>
        DateText.Length == 0 ? ""
        : DateModifierBox.SelectedIndex <= 0 ? DateText
        : s_dateModifiers[DateModifierBox.SelectedIndex].Label + " " + DateText;

    void TypeList_SelectionChanged(object sender, RoutedEventArgs e) =>
        SaveButton.IsEnabled = SelectedType is not null;

    void Save_Click(object sender, RoutedEventArgs e) =>
        DialogResult = true;

    void TypeList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (SelectedType is not null)
        {
            DialogResult = true;
        }
    }
}
