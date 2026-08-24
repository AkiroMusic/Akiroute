using System;
using Akiroute.Helpers;
using Akiroute.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

namespace Akiroute.Views.Controls;

/// <summary>
/// A single-row card for one proxy node: name, host:port detail line, and a
/// color-coded latency badge. Binds to the plain <see cref="ProxyNode"/> model
/// only; the parent page wires it to the view model and subscribes to
/// <see cref="NodeClicked"/>.
/// </summary>
public sealed partial class NodeCardControl : UserControl
{
    /// <summary>Backing store for <see cref="Node"/>.</summary>
    public static readonly DependencyProperty NodeProperty = DependencyProperty.Register(
        nameof(Node),
        typeof(ProxyNode),
        typeof(NodeCardControl),
        new PropertyMetadata(null, OnNodeChanged));

    /// <summary>Raised when the card is tapped.</summary>
    public event EventHandler? NodeClicked;

    /// <summary>Initializes a new instance of the <see cref="NodeCardControl"/> class.</summary>
    public NodeCardControl()
    {
        InitializeComponent();
    }

    /// <summary>The node this card displays. Changing it refreshes the card's visuals.</summary>
    public ProxyNode? Node
    {
        get => (ProxyNode?)GetValue(NodeProperty);
        set => SetValue(NodeProperty, value);
    }

    /// <summary>Called when <see cref="Node"/> changes; refreshes every visual from the new node.</summary>
    private static void OnNodeChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => ((NodeCardControl)d).Refresh();

    /// <summary>Refreshes the name, detail line, latency badge, and selection highlight from <see cref="Node"/>.</summary>
    private void Refresh()
    {
        var node = Node;
        if (node is null)
        {
            NameText.Text = string.Empty;
            DetailText.Text = string.Empty;
            UpdateBadge(-1);
            VisualStateManager.GoToState(this, "Unselected", true);
            return;
        }

        NameText.Text = node.Name ?? string.Empty;
        DetailText.Text = FormatDetail(node);
        UpdateBadge(node.PingMs);
        VisualStateManager.GoToState(this, node.IsSelected ? "Selected" : "Unselected", true);
    }

    /// <summary>Builds the "address:port" detail line, or an empty string when no address is set.</summary>
    private static string FormatDetail(ProxyNode node)
    {
        string address = node.Address ?? string.Empty;
        if (address.Length == 0)
        {
            return string.Empty;
        }

        return node.Port > 0 ? $"{address}:{node.Port}" : address;
    }

    /// <summary>Applies the latency color coding: &lt;100 ms green, &lt;300 ms caution, ≥300 ms critical, -1 untested.</summary>
    private void UpdateBadge(int pingMs)
    {
        string state;
        if (pingMs < 0)
        {
            LatencyText.Text = Loc.Get("NodeCardLatencyUntested");
            state = "LatencyUntested";
        }
        else if (pingMs < 100)
        {
            LatencyText.Text = $"{pingMs}ms";
            state = "LatencyGreen";
        }
        else if (pingMs < 300)
        {
            LatencyText.Text = $"{pingMs}ms";
            state = "LatencyCaution";
        }
        else
        {
            LatencyText.Text = $"{pingMs}ms";
            state = "LatencyCritical";
        }

        VisualStateManager.GoToState(this, state, true);
    }

    private void CardRoot_Tapped(object sender, TappedRoutedEventArgs e)
        => NodeClicked?.Invoke(this, EventArgs.Empty);

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

    private void OnGotFocus(object sender, RoutedEventArgs e)
        => VisualStateManager.GoToState(this, "Focused", true);

    private void OnLostFocus(object sender, RoutedEventArgs e)
        => VisualStateManager.GoToState(this, "Unfocused", true);
}
