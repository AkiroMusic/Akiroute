using System;
using Akiroute.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace Akiroute.Views.Controls;

/// <summary>
/// A single-row routing card for one process: icon, friendly name, and a
/// three-state segmented toggle (Proxy / Direct / Block). Binds to the plain
/// <see cref="ProcessInfoItem"/> model only; the parent page wires it to the
/// view model and subscribes to <see cref="ActionChanged"/>.
/// </summary>
public sealed partial class ProcessItemControl : UserControl
{
    /// <summary>Backing store for <see cref="Item"/>.</summary>
    public static readonly DependencyProperty ItemProperty = DependencyProperty.Register(
        nameof(Item),
        typeof(ProcessInfoItem),
        typeof(ProcessItemControl),
        new PropertyMetadata(null, OnItemChanged));

    /// <summary>Raised when the user picks a new routing action for <see cref="Item"/>.</summary>
    public event EventHandler<ProcessAction>? ActionChanged;

    // True while the segment selection is being updated programmatically from
    // Item, so the Checked handler does not write back into Item.Action.
    private bool _syncing;

    /// <summary>Initializes a new instance of the <see cref="ProcessItemControl"/> class.</summary>
    public ProcessItemControl()
    {
        InitializeComponent();

        // Each card's toggle must be independent, so give the segments a unique
        // group name instead of a shared literal (which would group all cards).
        string group = $"ProcessActionSegment_{Guid.NewGuid():N}";
        ProxySegment.GroupName = group;
        DirectSegment.GroupName = group;
        BlockSegment.GroupName = group;
    }

    /// <summary>The process this card displays. Changing it refreshes the card's visuals.</summary>
    public ProcessInfoItem? Item
    {
        get => (ProcessInfoItem?)GetValue(ItemProperty);
        set => SetValue(ItemProperty, value);
    }

    /// <summary>Called when <see cref="Item"/> changes; refreshes every visual from the new item.</summary>
    private static void OnItemChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => ((ProcessItemControl)d).Refresh();

    /// <summary>Refreshes icon, texts, dimming, and the segment selection from <see cref="Item"/>.</summary>
    private void Refresh()
    {
        var item = Item;
        if (item is null)
        {
            NameText.Text = string.Empty;
            DetailText.Text = string.Empty;
            IconImage.Source = null;
            IconImage.Visibility = Visibility.Collapsed;
            IconFallback.Visibility = Visibility.Visible;
            IconFallbackGlyph.Visibility = Visibility.Visible;
            IconFallbackLetter.Visibility = Visibility.Collapsed;
            CardRoot.Opacity = 1.0;
            _syncing = true;
            ProxySegment.IsChecked = false;
            DirectSegment.IsChecked = false;
            BlockSegment.IsChecked = false;
            _syncing = false;
            return;
        }

        UpdateIcon(item);
        NameText.Text = item.DisplayName;
        DetailText.Text = item.ProcessName;
        CardRoot.Opacity = item.IsSystem ? 0.55 : 1.0;
        SyncSegments(item.Action);
    }

    /// <summary>Shows the process icon, or a letter/glyph placeholder when it is unavailable.</summary>
    private void UpdateIcon(ProcessInfoItem item)
    {
        if (item.Icon is ImageSource source)
        {
            IconImage.Source = source;
            IconImage.Visibility = Visibility.Visible;
            IconFallback.Visibility = Visibility.Collapsed;
            return;
        }

        IconImage.Source = null;
        IconImage.Visibility = Visibility.Collapsed;
        IconFallback.Visibility = Visibility.Visible;

        string letter = string.IsNullOrWhiteSpace(item.DisplayName)
            ? string.Empty
            : item.DisplayName[..1].ToUpperInvariant();

        bool hasLetter = letter.Length > 0;
        IconFallbackLetter.Text = hasLetter ? letter : string.Empty;
        IconFallbackLetter.Visibility = hasLetter ? Visibility.Visible : Visibility.Collapsed;
        IconFallbackGlyph.Visibility = hasLetter ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>Sets the checked segment to match the given <see cref="ProcessAction"/>.</summary>
    private void SyncSegments(ProcessAction action)
    {
        _syncing = true;
        ProxySegment.IsChecked = action == ProcessAction.Proxy;
        DirectSegment.IsChecked = action == ProcessAction.Direct;
        BlockSegment.IsChecked = action == ProcessAction.Block;
        _syncing = false;
    }

    /// <summary>Applies the checked segment to <see cref="Item"/> and notifies subscribers.</summary>
    private void Segment_Checked(object sender, RoutedEventArgs e)
    {
        if (_syncing || Item is null || sender is not RadioButton { Tag: string tag })
        {
            return;
        }

        ProcessAction action = tag switch
        {
            "Direct" => ProcessAction.Direct,
            "Block" => ProcessAction.Block,
            _ => ProcessAction.Proxy,
        };

        // Deliberately do NOT write Item.Action here: the view model's SetAction
        // applies the change and persists the rules, and it guards on
        // item.Action == action. Writing it here first would make that guard
        // return early and RulesChanged would never fire (settings never saved).
        ActionChanged?.Invoke(this, action);
    }

    private void CardRoot_PointerEntered(object sender, PointerRoutedEventArgs e)
        => VisualStateManager.GoToState(this, "PointerOver", true);

    private void CardRoot_PointerExited(object sender, PointerRoutedEventArgs e)
        => VisualStateManager.GoToState(this, "Normal", true);

    private void CardRoot_PointerPressed(object sender, PointerRoutedEventArgs e)
        => VisualStateManager.GoToState(this, "Pressed", true);

    private void CardRoot_PointerReleased(object sender, PointerRoutedEventArgs e)
        => VisualStateManager.GoToState(this, "PointerOver", true);

    private void CardRoot_PointerCaptureLost(object sender, PointerRoutedEventArgs e)
        => VisualStateManager.GoToState(this, "Normal", true);
}
