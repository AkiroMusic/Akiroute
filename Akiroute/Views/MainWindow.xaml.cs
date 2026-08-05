using System;
using System.ComponentModel;
using System.Threading.Tasks;
using Akiroute.Helpers;
using Akiroute.Models;
using Akiroute.ViewModels;
using Akiroute.Views.Controls;
using Akiroute.Views.Dialogs;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

namespace Akiroute.Views;

/// <summary>
/// Main application window (plan Task 5.1/5.2). The controls are dependency
/// property and event driven — they know nothing about view models — so this
/// code-behind is the glue layer: it projects view-model state onto the control
/// dependency properties, routes control events back into view-model commands,
/// and hosts the import / edit dialogs.
/// </summary>
public sealed partial class MainWindow : Window
{
    private readonly MainViewModel _vm;
    private readonly Style _navItemStyle;
    private readonly Style _navItemSelectedStyle;

    /// <summary>The composed main view model; x:Bind targets this property.</summary>
    public MainViewModel Vm => _vm;

    /// <summary>
    /// Creates the window for the given view model. The App layer owns service
    /// construction; the window only receives the composed view model.
    /// </summary>
    /// <param name="vm">The main coordinator view model.</param>
    public MainWindow(MainViewModel vm)
    {
        _vm = vm ?? throw new ArgumentNullException(nameof(vm));
        InitializeComponent();
        Title = "Akiroute";

        Root.DataContext = _vm;

        _navItemStyle = (Style)Root.Resources["NavItemStyle"];
        _navItemSelectedStyle = (Style)Root.Resources["NavItemSelectedStyle"];

        // Brand logo: the PNG ships next to the engine assets in the output
        // directory; unpackaged apps resolve image sources from the exe folder.
        try
        {
            var logoPath = System.IO.Path.Combine(
                AppContext.BaseDirectory, "Assets", "Icons", "Akiroute-Logo.png");
            BrandLogoBrush.ImageSource = new BitmapImage(new Uri(logoPath));
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[MainWindow] Brand logo failed: {ex.Message}");
        }

        // The routing RadioButton raises a plain CLR event (not a routed one), so
        // subscribe per card as containers are realized, and unsubscribe on recycle.
        ProcessesList.ContainerContentChanging += ProcessesList_ContainerContentChanging;

        // Project the initial proxy state onto the status header DPs.
        PushStatus();
        _vm.Status.PropertyChanged += OnStatusPropertyChanged;

        // Persist the shared settings whenever process rules or imports change.
        _vm.Processes.RulesChanged += (_, _) => _vm.SaveSettings();
        _vm.Nodes.NodesImported += (_, _) => _vm.SaveSettings();

        Root.Loaded += OnRootLoaded;
    }

    private void OnRootLoaded(object sender, RoutedEventArgs e)
    {
        Root.Loaded -= OnRootLoaded;
        SelectNav("nodes");
        SyncSettingsControls();
        _ = RefreshProcessesAsync();
    }

    // ---- Sidebar navigation --------------------------------------------------

    private void NavItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string tag })
        {
            SelectNav(tag);
        }
    }

    private void SelectNav(string tag)
    {
        SetNavActive(NavNodesButton, tag == "nodes");
        SetNavActive(NavProcessesButton, tag == "processes");
        SetNavActive(NavSettingsButton, tag == "settings");

        NodesPanel.Visibility = tag == "nodes" ? Visibility.Visible : Visibility.Collapsed;
        ProcessesPanel.Visibility = tag == "processes" ? Visibility.Visible : Visibility.Collapsed;
        SettingsPanel.Visibility = tag == "settings" ? Visibility.Visible : Visibility.Collapsed;
    }

    private void SetNavActive(Button button, bool active)
        => button.Style = active ? _navItemSelectedStyle : _navItemStyle;

    // ---- Status header projection ----------------------------------------------

    private void OnStatusPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(ProxyStatusViewModel.IsRunning):
            case nameof(ProxyStatusViewModel.IsStarting):
            case nameof(ProxyStatusViewModel.StatusMessage):
            case nameof(ProxyStatusViewModel.LastError):
                PushStatus();
                break;
        }
    }

    /// <summary>Copies the proxy state onto the header DPs and the traffic card visibility.</summary>
    private void PushStatus()
    {
        StatusHeader.IsRunning = _vm.Status.IsRunning;
        StatusHeader.IsBusy = _vm.Status.IsStarting;
        StatusHeader.StatusMessage = _vm.Status.StatusMessage;
        StatusHeader.ErrorMessage = _vm.Status.LastError;
        TrafficCard.Visibility = _vm.Status.IsRunning ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void StatusHeader_ToggleRequested(object? sender, EventArgs e)
    {
        try
        {
            await _vm.ToggleProxyAsync();
        }
        catch (OperationCanceledException)
        {
            // The transition was cancelled; state is already consistent.
        }
        catch (Exception ex)
        {
            StatusHeader.ErrorMessage = ex.Message;
        }
    }

    // ---- Nodes panel ------------------------------------------------------------

    /// <summary>
    /// Handles the card's bubbling Tapped event at the ListView level and routes
    /// the tapped node into the selection command.
    /// </summary>
    private void NodesList_Tapped(object sender, TappedRoutedEventArgs e)
    {
        if (FindAncestor<NodeCardControl>(e.OriginalSource as DependencyObject) is { } card
            && card.Node is ProxyNode node
            && !ReferenceEquals(node, _vm.Nodes.SelectedNode))
        {
            _vm.Nodes.SelectNode(node);
            RefreshNodeCards();
        }
    }

    /// <summary>
    /// Re-applies the <see cref="NodeCardControl.Node"/> dependency property on every
    /// realized card so cards re-render when their (non-observable) model fields
    /// change — selection highlight, latency badge, and edited identity fields.
    /// </summary>
    private void RefreshNodeCards()
    {
        for (int i = 0; i < _vm.Nodes.Nodes.Count; i++)
        {
            // Content is the DATA item (ProxyNode); the visual card is the
            // template root. Matching Content here would never find a card and
            // node cards would never re-render after ping/edit/selection.
            if (NodesList.ContainerFromIndex(i) is ListViewItem { ContentTemplateRoot: NodeCardControl card })
            {
                card.ClearValue(NodeCardControl.NodeProperty);
                card.Node = _vm.Nodes.Nodes[i];
            }
        }
    }

    private async void OnPingAllClick(object sender, RoutedEventArgs e)
    {
        try
        {
            await _vm.Nodes.PingAllAsync();
            RefreshNodeCards();
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            StatusHeader.ErrorMessage = ex.Message;
        }
    }

    private async void OnImportClick(object sender, RoutedEventArgs e)
    {
        var dialog = new ImportDialog { XamlRoot = Content.XamlRoot };
        var result = await dialog.ShowAsync();
        if (result != ContentDialogResult.Primary)
        {
            return;
        }

        try
        {
            if (dialog.IsLinkMode)
            {
                await _vm.Nodes.ImportAsync(dialog.PastedText);
            }
            else
            {
                await _vm.Nodes.ImportSubscriptionAsync(dialog.SubscriptionUrl);
            }

            StatusHeader.ErrorMessage = _vm.Nodes.ImportError;
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            StatusHeader.ErrorMessage = ex.Message;
        }
    }

    private async void OnEditClick(object sender, RoutedEventArgs e)
    {
        var node = _vm.Nodes.SelectedNode;
        if (node is null)
        {
            StatusHeader.ErrorMessage = "请先选择一个节点";
            return;
        }

        var dialog = new EditNodeDialog(node) { XamlRoot = Content.XamlRoot };
        var result = await dialog.ShowAsync();
        if (result != ContentDialogResult.Primary)
        {
            return;
        }

        // The dialog edits a private copy; apply its result onto the live node.
        var edited = dialog.Node;
        node.Name = edited.Name;
        node.Type = edited.Type;
        node.Address = edited.Address;
        node.Port = edited.Port;
        node.ExtraParams = edited.ExtraParams;

        _vm.SaveSettings();
        RefreshNodeCards();
    }

    // ---- Processes panel --------------------------------------------------------

    private async Task RefreshProcessesAsync()
    {
        try
        {
            await _vm.Processes.RefreshAsync();
        }
        catch (OperationCanceledException)
        {
            // A cancelled refresh leaves the previous list content in place.
        }
    }

    private async void OnRefreshProcessesClick(object sender, RoutedEventArgs e)
    {
        try
        {
            await _vm.Processes.RefreshAsync();
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            StatusHeader.ErrorMessage = ex.Message;
        }
    }

    /// <summary>
    /// Subscribes (or unsubscribes) each realized <see cref="ProcessItemControl"/>
    /// card's <see cref="ProcessItemControl.ActionChanged"/> event as the ListView
    /// realizes or recycles its item containers.
    /// </summary>
    private void ProcessesList_ContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        // Content is the DATA item (ProcessInfoItem); the visual card is the
        // template root. Matching Content here would never find a card and the
        // ActionChanged subscription would silently never be established.
        if (args.ItemContainer.ContentTemplateRoot is not ProcessItemControl card)
        {
            return;
        }

        if (args.InRecycleQueue)
        {
            card.ActionChanged -= OnProcessActionChanged;
        }
        else
        {
            card.ActionChanged += OnProcessActionChanged;
        }
    }

    private void OnProcessActionChanged(object? sender, ProcessAction action)
    {
        if (sender is ProcessItemControl { Item: ProcessInfoItem item })
        {
            _vm.Processes.SetAction(item, action);
        }
    }

    // ---- Settings panel -----------------------------------------------------------

    /// <summary>Loads the current settings into the settings controls once, on load.</summary>
    private void SyncSettingsControls()
    {
        SetComboSelection(ModeCombo, _vm.Settings.Mode.ToString());
        SetComboSelection(ThemeCombo, _vm.Settings.Theme.ToString());
        PortBox.Text = _vm.Settings.Port.ToString();
        MinutesBox.Text = _vm.Settings.SubscriptionAutoUpdateMinutes.ToString();
        AutoConnectToggle.IsOn = _vm.Settings.AutoConnect;
        TunToggle.IsOn = _vm.Settings.TunEnabled;
    }

    private static void SetComboSelection(ComboBox combo, string tag)
    {
        foreach (var item in combo.Items)
        {
            if (item is ComboBoxItem { Tag: string t } && string.Equals(t, tag, StringComparison.Ordinal))
            {
                combo.SelectedItem = item;
                return;
            }
        }
    }

    private void ModeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ModeCombo.SelectedItem is ComboBoxItem { Tag: string tag }
            && Enum.TryParse<ProxyMode>(tag, out var mode))
        {
            _vm.Settings.Mode = mode;
        }
    }

    private void ThemeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ThemeCombo.SelectedItem is ComboBoxItem { Tag: string tag }
            && Enum.TryParse<AppTheme>(tag, out var theme))
        {
            _vm.Settings.Theme = theme;
            ThemeHelper.ApplyThemeSafe(this, theme);
        }
    }

    private void PortBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (int.TryParse(PortBox.Text.Trim(), out int port))
        {
            _vm.Settings.Port = port;
        }
    }

    private void MinutesBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (int.TryParse(MinutesBox.Text.Trim(), out int minutes) && minutes >= 0)
        {
            _vm.Settings.SubscriptionAutoUpdateMinutes = minutes;
        }
    }

    private void AutoConnectToggle_Toggled(object sender, RoutedEventArgs e)
        => _vm.Settings.AutoConnect = AutoConnectToggle.IsOn;

    private void TunToggle_Toggled(object sender, RoutedEventArgs e)
        => _vm.Settings.TunEnabled = TunToggle.IsOn;

    private void OnSaveSettingsClick(object sender, RoutedEventArgs e)
    {
        _vm.Settings.Save();
        StatusHeader.ErrorMessage = _vm.Settings.SaveError;
    }

    // ---- Helpers ------------------------------------------------------------------

    /// <summary>
    /// Walks up the visual (then logical) parent chain looking for an ancestor of
    /// type <typeparamref name="T"/>. Used to recover the card control behind an
    /// event whose <see cref="RoutedEventArgs.OriginalSource"/> is a deep child
    /// (the tapped border of a <see cref="NodeCardControl"/>, the radio button of a
    /// <see cref="ProcessItemControl"/>).
    /// </summary>
    private static T? FindAncestor<T>(DependencyObject? current)
        where T : DependencyObject
    {
        while (current is not null)
        {
            if (current is T match)
            {
                return match;
            }

            current = VisualTreeHelper.GetParent(current)
                ?? (current as FrameworkElement)?.Parent as DependencyObject;
        }

        return null;
    }
}
