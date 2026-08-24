using System;
using System.ComponentModel;
using System.Threading.Tasks;
using Akiroute.Helpers;
using Akiroute.Models;
using Akiroute.Services;
using Akiroute.ViewModels;
using Akiroute.Views.Controls;
using Akiroute.Views.Dialogs;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage.Pickers;

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

        // Capture nav styles from the buttons that declare them in XAML. Native
        // AOT projects ResourceDictionary lookups as DependencyObject (breaking
        // direct Style casts), while applied element properties stay typed and
        // AOT-safe. NavNodesButton declares the Selected variant because it is
        // the initially-active item.
        _navItemStyle = NavProcessesButton.Style;
        _navItemSelectedStyle = NavNodesButton.Style;

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

        // Subscription rows need date formatting on realization.
        SubscriptionsList.ContainerContentChanging += SubscriptionsList_ContainerContentChanging;

        // Project the initial proxy state onto the status header DPs.
        PushStatus();
        _vm.Status.PropertyChanged += OnStatusPropertyChanged;

        // Persist the shared settings whenever process rules or imports change.
        _vm.Processes.RulesChanged += (_, _) => _vm.SaveSettings();
        _vm.Nodes.NodesImported += (_, _) =>
        {
            _vm.SaveSettings();
            _vm.Settings.RefreshSubscriptionsView();
            UpdateSubscriptionsEmptyHint();
        };

        // Empty-state hint must track the LIVE collection (import/delete), not a
        // one-shot load-time evaluation.
        _vm.Nodes.Nodes.CollectionChanged += (_, _) => UpdateNodesEmptyHint();
        UpdateNodesEmptyHint();

        Root.Loaded += OnRootLoaded;
    }

    /// <summary>Shows the nodes empty-state hint only when no nodes exist.</summary>
    private void UpdateNodesEmptyHint()
        => NodesEmptyHint.Visibility = _vm.Nodes.Nodes.Count == 0
            ? Visibility.Visible
            : Visibility.Collapsed;

    private void OnRootLoaded(object sender, RoutedEventArgs e)
    {
        Root.Loaded -= OnRootLoaded;
        SelectNav("nodes");
        SyncSettingsControls();
        _vm.Settings.RefreshSubscriptionsView();
        UpdateSubscriptionsEmptyHint();
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
        SetNavActive(NavLogsButton, tag == "logs");
        SetNavActive(NavSettingsButton, tag == "settings");

        NodesPanel.Visibility = tag == "nodes" ? Visibility.Visible : Visibility.Collapsed;
        ProcessesPanel.Visibility = tag == "processes" ? Visibility.Visible : Visibility.Collapsed;
        LogsPanel.Visibility = tag == "logs" ? Visibility.Visible : Visibility.Collapsed;
        SettingsPanel.Visibility = tag == "settings" ? Visibility.Visible : Visibility.Collapsed;

        // Fade-in the newly visible panel (~188ms opacity 0→1). The panels stay
        // in the visual tree across switches, so EntranceThemeTransition never
        // re-fires — this code-behind storyboard replaces it on every switch.
        if (tag == "nodes")
        {
            FadeInPanel(NodesPanel);
        }
        else if (tag == "processes")
        {
            FadeInPanel(ProcessesPanel);
        }
        else if (tag == "logs")
        {
            _vm.Logs.Refresh();
            FadeInPanel(LogsPanel);
        }
        else if (tag == "settings")
        {
            FadeInPanel(SettingsPanel);
        }
    }

    /// <summary>
    /// Runs a brief opacity fade-in (0→1, ~188 ms) on the given panel.
    /// Must be called synchronously after setting Visibility = Visible so the
    /// initial opacity = 0 is applied before the next frame renders.
    /// </summary>
    private static void FadeInPanel(FrameworkElement panel)
    {
        panel.Opacity = 0;
        var animation = new DoubleAnimation
        {
            From = 0d,
            To = 1d,
            Duration = new Duration(TimeSpan.FromMilliseconds(188)),
        };
        Storyboard.SetTarget(animation, panel);
        Storyboard.SetTargetProperty(animation, "Opacity");
        var sb = new Storyboard();
        sb.Children.Add(animation);
        sb.Begin();
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
            StatusHeader.ErrorMessage = Loc.Get("Error.PleaseSelectNode");
            return;
        }

        await ShowEditNodeDialog(node);
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
        PingMinutesBox.Text = _vm.Settings.AutoPingMinutes.ToString();
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

    private void PingMinutesBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (int.TryParse(PingMinutesBox.Text.Trim(), out int minutes) && minutes >= 0)
        {
            _vm.Settings.AutoPingMinutes = minutes;
        }
    }

    /// <summary>Updates the per-subscription auto-update interval override.</summary>
    private void SubAutoUpdateBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (sender is TextBox { DataContext: SubscriptionEntry entry }
            && int.TryParse(((TextBox)sender).Text.Trim(), out int minutes)
            && minutes >= 0)
        {
            entry.AutoUpdateMinutes = minutes;
            _vm.SaveSettings();
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

    // ---- Subscription management ------------------------------------------------

    /// <summary>
    /// Handles the "删除" ghost button click inside each subscription row;
    /// extracts the <see cref="SubscriptionEntry"/> from the button's
    /// <see cref="FrameworkElement.DataContext"/> and forwards it to the remove command.
    /// </summary>
    private void OnRemoveSubscriptionClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: SubscriptionEntry entry })
        {
            _vm.Settings.RemoveSubscriptionCommand.Execute(entry);
            UpdateSubscriptionsEmptyHint();
        }
    }

    /// <summary>Formats the last-updated date text for each realized subscription row.</summary>
    private void SubscriptionsList_ContainerContentChanging(
        ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        if (args.ItemContainer.ContentTemplateRoot is Grid grid
            && args.Item is SubscriptionEntry entry
            && grid.FindName("LastUpdatedText") is TextBlock dateText)
        {
            dateText.Text = entry.LastUpdated.HasValue
                ? $" · {entry.LastUpdated.Value:yyyy-MM-dd}"
                : string.Empty;
        }
    }

    /// <summary>Shows the subscriptions empty-state hint only when no entries exist.</summary>
    private void UpdateSubscriptionsEmptyHint()
        => SubscriptionsEmptyHint.Visibility = _vm.Settings.SubscriptionEntries.Count == 0
            ? Visibility.Visible
            : Visibility.Collapsed;

    // ---- Config backup / restore -------------------------------------------------

    /// <summary>
    /// Exports the live config to a user-chosen JSON file. The picker is
    /// initialized with the WinRT interop HWND bridge (unpackaged WinUI 3
    /// requirement).
    /// </summary>
    private async void OnBackupConfigClick(object sender, RoutedEventArgs e)
    {
        var picker = new FileSavePicker();
        // Unpackaged WinUI 3: the picker must be parented to the window handle.
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);

        picker.SuggestedFileName = $"akiroute-config-{DateTime.Now:yyyyMMdd-HHmmss}";
        picker.FileTypeChoices.Add("JSON", [".json"]);

        var file = await picker.PickSaveFileAsync();
        if (file is null) return;

        try
        {
            ConfigBackupService.ExportToFile(_vm.Settings.Settings, file.Path);

            var dialog = new ContentDialog
            {
                Title = Loc.Get("Dialog.BackupSuccess"),
                Content = string.Format(Loc.Get("Dialog.BackupSuccessDetail"), file.Path),
                CloseButtonText = Loc.Get("Dialog.Ok"),
                XamlRoot = Content.XamlRoot,
            };
            await dialog.ShowAsync();
        }
        catch (Exception ex)
        {
            var dialog = new ContentDialog
            {
                Title = Loc.Get("Dialog.BackupFail"),
                Content = ex.Message,
                CloseButtonText = Loc.Get("Dialog.Ok"),
                XamlRoot = Content.XamlRoot,
            };
            await dialog.ShowAsync();
        }
    }

    /// <summary>
    /// Imports a backup JSON file and overwrites the live config atomically.
    /// In-session VMs keep the old state until restart — any post-restore
    /// in-app save would overwrite the restored file with stale VM state,
    /// so a prompt restart is required (documented known limitation).
    /// </summary>
    private async void OnRestoreConfigClick(object sender, RoutedEventArgs e)
    {
        var picker = new FileOpenPicker();
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);

        picker.FileTypeFilter.Add(".json");

        var file = await picker.PickSingleFileAsync();
        if (file is null) return;

        var restored = ConfigBackupService.ImportFromFile(file.Path);
        if (restored is null)
        {
            var errorDialog = new ContentDialog
            {
                Title = Loc.Get("Dialog.RestoreFail"),
                Content = Loc.Get("Dialog.RestoreFailInvalid"),
                CloseButtonText = Loc.Get("Dialog.Ok"),
                XamlRoot = Content.XamlRoot,
            };
            await errorDialog.ShowAsync();
            return;
        }

        // Write restored config to the live settings path atomically. The save
        // (and the success dialog that must only appear after it) is guarded so
        // an I/O failure surfaces as a dialog instead of crashing the process
        // from this async void handler.
        try
        {
            SettingsService.Save(restored);

            var dialog = new ContentDialog
            {
                Title = Loc.Get("Dialog.RestoreSuccess"),
                Content = Loc.Get("Dialog.RestoreSuccessDetail"),
                CloseButtonText = Loc.Get("Dialog.Ok"),
                XamlRoot = Content.XamlRoot,
            };
            await dialog.ShowAsync();
        }
        catch (Exception ex)
        {
            var errorDialog = new ContentDialog
            {
                Title = Loc.Get("Dialog.RestoreFail"),
                Content = string.Format(Loc.Get("Dialog.RestoreFailDetail"), ex.Message),
                CloseButtonText = Loc.Get("Dialog.Ok"),
                XamlRoot = Content.XamlRoot,
            };
            await errorDialog.ShowAsync();
        }
    }

    // ---- Node context menu -------------------------------------------------------

    private async void OnNodeEditContextClick(object sender, RoutedEventArgs e)
    {
        if (sender is MenuFlyoutItem { DataContext: ProxyNode node })
        {
            await ShowEditNodeDialog(node);
        }
    }

    private void OnNodeDeleteContextClick(object sender, RoutedEventArgs e)
    {
        if (sender is MenuFlyoutItem { DataContext: ProxyNode node })
        {
            // [RelayCommand] generates a public IRelayCommand<ProxyNode> property
            // even when the annotated method is private.
            _vm.Nodes.DeleteNodeCommand.Execute(node);
        }
    }

    private void OnNodeCopyAddressClick(object sender, RoutedEventArgs e)
    {
        if (sender is MenuFlyoutItem { DataContext: ProxyNode { Address: { } address } })
        {
            var package = new DataPackage();
            package.SetText(address);
            Clipboard.SetContent(package);
        }
    }

    private void OnNodeCopyLinkClick(object sender, RoutedEventArgs e)
    {
        if (sender is MenuFlyoutItem { DataContext: ProxyNode { RawConfig: { } raw } })
        {
            var package = new DataPackage();
            package.SetText(raw);
            Clipboard.SetContent(package);
        }
    }

    // ---- Logs panel -----------------------------------------------------------

    private void OnCopyAppLogClick(object sender, RoutedEventArgs e)
    {
        var text = _vm.Logs.AppLogText;
        if (!string.IsNullOrEmpty(text))
        {
            var package = new DataPackage();
            package.SetText(text);
            Clipboard.SetContent(package);
        }
    }

    private void OnCopyEngineLogClick(object sender, RoutedEventArgs e)
    {
        var text = _vm.Logs.EngineLogText;
        if (!string.IsNullOrEmpty(text))
        {
            var package = new DataPackage();
            package.SetText(text);
            Clipboard.SetContent(package);
        }
    }

    private void OnRefreshLogsClick(object sender, RoutedEventArgs e)
    {
        _vm.Logs.Refresh();
    }

    // ---- Process context menu -----------------------------------------------------

    private void OnProcessActionContextClick(object sender, RoutedEventArgs e)
    {
        if (sender is MenuFlyoutItem { DataContext: ProcessInfoItem item, Tag: string tag })
        {
            ProcessAction action = tag switch
            {
                "Direct" => ProcessAction.Direct,
                "Block" => ProcessAction.Block,
                _ => ProcessAction.Proxy,
            };
            _vm.Processes.SetAction(item, action);
        }
    }

    // ---- Shared dialog flows ------------------------------------------------------

    /// <summary>
    /// Shared edit-node flow used by both the toolbar button and the context menu.
    /// </summary>
    private async Task ShowEditNodeDialog(ProxyNode node)
    {
        var dialog = new EditNodeDialog(node) { XamlRoot = Content.XamlRoot };
        var result = await dialog.ShowAsync();
        if (result != ContentDialogResult.Primary)
        {
            return;
        }

        var edited = dialog.Node;
        node.Name = edited.Name;
        node.Type = edited.Type;
        node.Address = edited.Address;
        node.Port = edited.Port;
        node.ExtraParams = edited.ExtraParams;

        _vm.SaveSettings();
        RefreshNodeCards();
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
