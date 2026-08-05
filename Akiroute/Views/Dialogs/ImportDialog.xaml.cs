using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Akiroute.Views.Dialogs;

/// <summary>
/// Dialog that collects an import source: either pasted node links / Clash YAML
/// text, or a subscription feed URL. The caller reads <see cref="PastedText"/> or
/// <see cref="SubscriptionUrl"/> (whichever matches the active mode) after the
/// dialog closes with the primary button.
/// </summary>
public sealed partial class ImportDialog : ContentDialog
{
    /// <summary>Initializes a new instance of the <see cref="ImportDialog"/> class.</summary>
    public ImportDialog()
    {
        InitializeComponent();

        // Select the paste mode by default. Doing it here (after InitializeComponent)
        // guarantees every named element exists before the Checked handler runs.
        LinkMode.IsChecked = true;
    }

    /// <summary>True when the paste-links mode is active.</summary>
    public bool IsLinkMode => LinkMode.IsChecked == true;

    /// <summary>Pasted link/YAML text; null unless the paste mode is active.</summary>
    public string? PastedText => IsLinkMode ? PasteBox.Text.Trim() : null;

    /// <summary>Subscription feed URL; null unless the URL mode is active.</summary>
    public string? SubscriptionUrl => !IsLinkMode ? UrlBox.Text.Trim() : null;

    private void Mode_Checked(object sender, RoutedEventArgs e)
    {
        bool link = IsLinkMode;
        PasteBox.Visibility = link ? Visibility.Visible : Visibility.Collapsed;
        UrlBox.Visibility = link ? Visibility.Collapsed : Visibility.Visible;
        UpdateCanSubmit();
    }

    private void Input_TextChanged(object sender, TextChangedEventArgs e)
        => UpdateCanSubmit();

    /// <summary>Enables the primary button only when the active mode has non-empty input.</summary>
    private void UpdateCanSubmit()
    {
        string input = IsLinkMode ? PasteBox.Text : UrlBox.Text;
        IsPrimaryButtonEnabled = !string.IsNullOrWhiteSpace(input);
    }
}
