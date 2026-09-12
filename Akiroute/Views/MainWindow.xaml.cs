using System;
using System.ComponentModel;
using System.Threading.Tasks;
using Akiroute.Helpers;
using Akiroute.Models;
using Akiroute.Services;
using Akiroute.ViewModels;
using Akiroute.Views.Controls;
using Akiroute.Views.Dialogs;
using Microsoft.UI.Dispatching;
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

        // Node cards raise NodeClicked on keyboard activation (Enter/Space);
        // pointer taps flow through NodesList_Tapped below.
        NodesList.ContainerContentChanging += NodesList_ContainerContentChanging;

        // Subscription rows need date formatting on realization.
        SubscriptionsList.ContainerContentChanging += SubscriptionsList_ContainerContentChanging;

        // Project the initial proxy state onto the status header DPs.
        PushStatus();
        _vm.Status.PropertyChanged += OnStatusPropertyChanged;

        // Footer version tracks the assembly version.
        FooterVersionText.Text = $"v{typeof(MainWindow).Assembly.GetName().Version}";

        // Persist the shared settings whenever process rules or imports change.
        _vm.Processes.RulesChanged += (_, _) => _vm.SaveSettings();
        _vm.Nodes.NodesImported += (_, _) =>
        {
            _vm.SaveSettings();
            _vm.Settings.RefreshSubscriptionsView();
            UpdateSubscriptionsEmptyHint();
        };

        // Auto-persist whenever an editable setting changes; this also keeps
        // the launch-on-startup registry entry and the persisted flag
        // consistent. The Save button remains as the explicit fallback and
        // surfaces SaveError.
        _vm.Settings.SettingsChanged += (_, _) => _vm.SaveSettings();

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

    /// <summary>Guards <see cref="InitializeUiState"/> against double execution.</summary>
    private bool _uiStateInitialized;

    private void OnRootLoaded(object sender, RoutedEventArgs e)
    {
        Root.Loaded -= OnRootLoaded;
        InitializeUiState();
    }

    /// <summary>
    /// One-shot UI-state initialization: nav selection, settings-control sync,
    /// subscription view, and the first process scan. Normally triggered by
    /// Root.Loaded, but ALSO invoked explicitly by App when the window starts
    /// hidden (StartMinimized) — a never-activated window may not raise Loaded
    /// until it is first shown, which would defer initialization until the
    /// user restores it from the tray.
    /// </summary>
    internal void InitializeUiState()
    {
        if (_uiStateInitialized)
        {
            return;
        }

        _uiStateInitialized = true;
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
            _ = RefreshLogsSafeAsync();
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
    /// Subscribes (or unsubscribes) each realized <see cref="NodeCardControl"/>
    /// card's keyboard-activation event as the ListView realizes or recycles
    /// its item containers.
    /// </summary>
    private void NodesList_ContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        if (args.ItemContainer.ContentTemplateRoot is not NodeCardControl card)
        {
            return;
        }

        if (args.InRecycleQueue)
        {
            card.NodeClicked -= OnNodeCardActivated;
        }
        else
        {
            card.NodeClicked += OnNodeCardActivated;
        }
    }

    private void OnNodeCardActivated(object? sender, EventArgs e)
    {
        if (sender is NodeCardControl { Node: ProxyNode node }
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
        // async void: the dialog show itself can throw, so the whole flow is
        // guarded and failures surface through the status header.
        try
        {
            var dialog = new ImportDialog { XamlRoot = Content.XamlRoot };
            var result = await dialog.ShowAsync();
            if (result != ContentDialogResult.Primary)
            {
                return;
            }

            if (dialog.IsLinkMode)
            {
                await _vm.Nodes.ImportAsync(dialog.PastedText);
            }
            else
            {
                // Plain-HTTP feeds carry node credentials in the clear and can
                // be tampered with in transit — require an explicit opt-in.
                if (dialog.SubscriptionUrl?.StartsWith("http://", StringComparison.OrdinalIgnoreCase) == true
                    && !await ConfirmInsecureSubscriptionAsync(dialog.SubscriptionUrl))
                {
                    return;
                }

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

    /// <summary>
    /// Shows the plain-HTTP subscription confirmation. Returns true only when
    /// the user explicitly continues.
    /// </summary>
    private async Task<bool> ConfirmInsecureSubscriptionAsync(string url)
    {
        var confirm = new ContentDialog
        {
            Title = Loc.Get("Dialog.HttpSubscribeTitle"),
            Content = string.Format(Loc.Get("Dialog.HttpSubscribeBody"), url),
            PrimaryButtonText = Loc.Get("Dialog.HttpSubscribeContinue"),
            CloseButtonText = Loc.Get("Dialog.Cancel"),
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = Content.XamlRoot,
        };
        return await confirm.ShowAsync() == ContentDialogResult.Primary;
    }

    private async void OnEditClick(object sender, RoutedEventArgs e)
    {
        var node = _vm.Nodes.SelectedNode;
        if (node is null)
        {
            StatusHeader.ErrorMessage = Loc.Get("Error.PleaseSelectNode");
            return;
        }

        // async void: an escaping exception would crash the process.
        try
        {
            await ShowEditNodeDialog(node);
        }
        catch (Exception ex)
        {
            StatusHeader.ErrorMessage = ex.Message;
        }
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
        StartMinimizedToggle.IsOn = _vm.Settings.StartMinimized;
        LaunchOnStartupToggle.IsOn = _vm.Settings.LaunchOnStartup;
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
        // Accept only values the settings view model keeps (1024-65535);
        // anything else shows inline feedback.
        var valid = int.TryParse(PortBox.Text.Trim(), out int port) && port is >= 1024 and <= 65535;
        PortErrorText.Visibility = valid ? Visibility.Collapsed : Visibility.Visible;
        if (valid)
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

    /// <summary>
    /// Debounce timer for the per-subscription interval box; the settings save
    /// runs 400 ms after typing pauses.
    /// </summary>
    private DispatcherQueueTimer? _subAutoUpdateSaveTimer;

    /// <summary>Updates the per-subscription auto-update interval override.</summary>
    private void SubAutoUpdateBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (sender is TextBox { DataContext: SubscriptionEntry entry }
            && int.TryParse(((TextBox)sender).Text.Trim(), out int minutes)
            && minutes >= 0)
        {
            entry.AutoUpdateMinutes = minutes;
            DebounceSubscriptionSave();
        }
    }

    /// <summary>Schedules the settings save 400 ms after the last keystroke.</summary>
    private void DebounceSubscriptionSave()
    {
        _subAutoUpdateSaveTimer ??= DispatcherQueue.CreateTimer();
        _subAutoUpdateSaveTimer.Interval = TimeSpan.FromMilliseconds(400);
        _subAutoUpdateSaveTimer.IsRepeating = false;
        _subAutoUpdateSaveTimer.Tick -= OnSubAutoUpdateSaveTimerTick;
        _subAutoUpdateSaveTimer.Tick += OnSubAutoUpdateSaveTimerTick;
        _subAutoUpdateSaveTimer.Stop();
        _subAutoUpdateSaveTimer.Start();
    }

    private void OnSubAutoUpdateSaveTimerTick(DispatcherQueueTimer sender, object args)
        => _vm.SaveSettings();

    private void AutoConnectToggle_Toggled(object sender, RoutedEventArgs e)
        => _vm.Settings.AutoConnect = AutoConnectToggle.IsOn;

    private void StartMinimizedToggle_Toggled(object sender, RoutedEventArgs e)
        => _vm.Settings.StartMinimized = StartMinimizedToggle.IsOn;

    private void LaunchOnStartupToggle_Toggled(object sender, RoutedEventArgs e)
        => _vm.Settings.LaunchOnStartup = LaunchOnStartupToggle.IsOn;

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
            // Serialize + write off the UI thread; the dialog below marshals
            // back through the await continuation.
            await Task.Run(() => ConfigBackupService.ExportToFile(_vm.Settings.Settings, file.Path));

            var dialog = new ContentDialog
            {
                Title = Loc.Get("Dialog.BackupSuccess"),
                // Backups are plaintext by design — the user must be told the
                // file carries node credentials in the clear.
                Content = string.Format(Loc.Get("Dialog.BackupSuccessDetail"), file.Path)
                          + Environment.NewLine + Environment.NewLine
                          + Loc.Get("Dialog.BackupPlaintextWarning"),
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
    /// After the file is written, the restored snapshot is ALSO applied to the
    /// live in-memory settings (and every settings-driven UI surface re-syncs) —
    /// otherwise the close-time window-bounds save would overwrite the restored
    /// file with the stale pre-restore state.
    /// </summary>
    private async void OnRestoreConfigClick(object sender, RoutedEventArgs e)
    {
        try
        {
            await RestoreConfigCoreAsync();
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

    private async Task RestoreConfigCoreAsync()
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

        // Write restored config to the live settings path atomically, off the
        // UI thread; an I/O failure surfaces as a dialog instead of crashing
        // this async void handler.
        try
        {
            await Task.Run(() => SettingsService.Save(restored));
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
            return;
        }

        // Apply the restored snapshot to the live settings instance (mutated
        // in place — view models hold references to it), then re-sync every
        // settings-driven UI surface.
        App.ApplyRestoredSettings(restored);
        StartupHelper.ReconcileLaunchOnStartup(restored.LaunchOnStartup);
        _vm.Settings.RefreshWrappersFromSettings();
        SyncSettingsControls();
        _vm.Nodes.ReloadFromSettings();
        UpdateSubscriptionsEmptyHint();
        UpdateNodesEmptyHint();

        var dialog = new ContentDialog
        {
            Title = Loc.Get("Dialog.RestoreSuccess"),
            Content = Loc.Get("Dialog.RestoreSuccessDetail"),
            CloseButtonText = Loc.Get("Dialog.Ok"),
            XamlRoot = Content.XamlRoot,
        };
        await dialog.ShowAsync();
    }

    // ---- Node context menu -------------------------------------------------------

    private async void OnNodeEditContextClick(object sender, RoutedEventArgs e)
    {
        if (sender is MenuFlyoutItem { DataContext: ProxyNode node })
        {
            // async void: an escaping exception would crash the process.
            try
            {
                await ShowEditNodeDialog(node);
            }
            catch (Exception ex)
            {
                StatusHeader.ErrorMessage = ex.Message;
            }
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
        => _ = RefreshLogsSafeAsync();

    /// <summary>Refreshes both log views off the UI thread; never throws.</summary>
    private async Task RefreshLogsSafeAsync()
    {
        try
        {
            await _vm.Logs.RefreshAsync();
        }
        catch (Exception ex)
        {
            StatusHeader.ErrorMessage = ex.Message;
        }
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
