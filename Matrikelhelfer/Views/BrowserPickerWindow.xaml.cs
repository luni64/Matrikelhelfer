using System.Collections.Generic;
using System.Windows;
using MahApps.Metro.Controls;
using Matrikelhelfer.Services;

namespace Matrikelhelfer.Views;

public partial class BrowserPickerWindow : MetroWindow
{
    public BrowserCandidate? Chosen { get; private set; }

    public BrowserPickerWindow(IReadOnlyList<BrowserCandidate> candidates)
    {
        InitializeComponent();
        BrowserList.ItemsSource = candidates;
        if (candidates.Count > 0)
        {
            BrowserList.SelectedIndex = 0;
        }
    }

    void Connect_Click(object sender, RoutedEventArgs e)
    {
        if (BrowserList.SelectedItem is BrowserCandidate candidate)
        {
            Chosen = candidate;
            DialogResult = true;
        }
    }

    void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }
}
