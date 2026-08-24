using System;
using Akiroute.Helpers;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Akiroute.Views.Controls;

/// <summary>
/// Top status bar with a one-click start/stop toggle. Fully dependency-driven
/// and self-contained: it exposes state via dependency properties and raises
/// <see cref="ToggleRequested"/> when the user clicks the toggle; the parent
/// page wires those to the proxy view model. No view-model references here.
/// </summary>
public sealed partial class StatusHeaderControl : UserControl
{
    /// <summary>Backing store for <see cref="IsRunning"/>.</summary>
    public static readonly DependencyProperty IsRunningProperty = DependencyProperty.Register(
        nameof(IsRunning),
        typeof(bool),
        typeof(StatusHeaderControl),
        new PropertyMetadata(false, OnStateChanged));

    /// <summary>Backing store for <see cref="IsBusy"/>.</summary>
    public static readonly DependencyProperty IsBusyProperty = DependencyProperty.Register(
        nameof(IsBusy),
        typeof(bool),
        typeof(StatusHeaderControl),
        new PropertyMetadata(false, OnStateChanged));

    /// <summary>Backing store for <see cref="StatusMessage"/>.</summary>
    public static readonly DependencyProperty StatusMessageProperty = DependencyProperty.Register(
        nameof(StatusMessage),
        typeof(string),
        typeof(StatusHeaderControl),
        new PropertyMetadata(null, OnStateChanged));

    /// <summary>Backing store for <see cref="ErrorMessage"/>.</summary>
    public static readonly DependencyProperty ErrorMessageProperty = DependencyProperty.Register(
        nameof(ErrorMessage),
        typeof(string),
        typeof(StatusHeaderControl),
        new PropertyMetadata(null, OnStateChanged));

    /// <summary>Backing store for <see cref="Title"/>.</summary>
    public static readonly DependencyProperty TitleProperty = DependencyProperty.Register(
        nameof(Title),
        typeof(string),
        typeof(StatusHeaderControl),
        new PropertyMetadata("Akiroute", OnStateChanged));

    /// <summary>Raised when the user clicks the start/stop toggle button.</summary>
    public event EventHandler? ToggleRequested;

    /// <summary>Initializes a new instance of the <see cref="StatusHeaderControl"/> class.</summary>
    public StatusHeaderControl()
    {
        InitializeComponent();
    }

    /// <summary>True while the proxy engine is running; drives the toggle visual and text.</summary>
    public bool IsRunning
    {
        get => (bool)GetValue(IsRunningProperty);
        set => SetValue(IsRunningProperty, value);
    }

    /// <summary>True while a start/stop transition is in flight; disables the toggle button.</summary>
    public bool IsBusy
    {
        get => (bool)GetValue(IsBusyProperty);
        set => SetValue(IsBusyProperty, value);
    }

    /// <summary>Secondary status line (e.g. "正在连接节点…"). Hidden when null or empty.</summary>
    public string? StatusMessage
    {
        get => (string?)GetValue(StatusMessageProperty);
        set => SetValue(StatusMessageProperty, value);
    }

    /// <summary>Error line shown in the danger color when set.</summary>
    public string? ErrorMessage
    {
        get => (string?)GetValue(ErrorMessageProperty);
        set => SetValue(ErrorMessageProperty, value);
    }

    /// <summary>App title shown on the left of the bar. Defaults to "Akiroute".</summary>
    public string? Title
    {
        get => (string?)GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    /// <summary>Refreshes every visual when any state dependency property changes.</summary>
    private static void OnStateChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => ((StatusHeaderControl)d).UpdateState();

    private void UpdateState()
    {
        // Title.
        TitleText.Text = string.IsNullOrWhiteSpace(Title) ? "Akiroute" : Title;

        // Status line.
        StatusText.Text = StatusMessage ?? string.Empty;
        StatusText.Visibility = string.IsNullOrEmpty(StatusMessage)
            ? Visibility.Collapsed
            : Visibility.Visible;

        // Error line.
        ErrorText.Text = ErrorMessage ?? string.Empty;
        ErrorText.Visibility = string.IsNullOrEmpty(ErrorMessage)
            ? Visibility.Collapsed
            : Visibility.Visible;

        // Toggle glyph/text and running dot.
        ToggleGlyph.Glyph = IsRunning ? "\uE71A" : "\uE768";
        ToggleText.Text = IsRunning ? Loc.Get("Status.ToggleStop") : Loc.Get("Status.ToggleStart");
        DotIndicator.Visibility = IsRunning ? Visibility.Visible : Visibility.Collapsed;

        _ = VisualStateManager.GoToState(this, IsRunning ? "Running" : "Stopped", true);
        _ = VisualStateManager.GoToState(this, IsBusy ? "Busy" : "Idle", true);
    }

    /// <summary>Raises <see cref="ToggleRequested"/> unless a transition is already in flight.</summary>
    private void ToggleButton_Click(object sender, RoutedEventArgs e)
    {
        if (IsBusy)
        {
            return;
        }

        ToggleRequested?.Invoke(this, EventArgs.Empty);
    }
}
