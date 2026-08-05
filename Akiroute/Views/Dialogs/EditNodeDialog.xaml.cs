using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Nodes;
using Akiroute.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Akiroute.Views.Dialogs;

/// <summary>
/// Dialog that edits a node's identity fields and its protocol-specific
/// <see cref="ProxyNode.ExtraParams"/> as raw JSON. Edits are applied to a
/// private copy of the source node; the caller reads <see cref="Node"/> after the
/// dialog closes with the primary button. JSON is handled exclusively through
/// <see cref="System.Text.Json.Nodes.JsonNode"/> (AOT-safe, no reflection).
/// </summary>
public sealed partial class EditNodeDialog : ContentDialog
{
    /// <summary>The six protocols supported by the Xray config builder.</summary>
    private static readonly string[] KnownTypes = ["vless", "vmess", "ss", "trojan", "hysteria2", "tuic"];

    /// <summary>The node being edited; mutated only when validation passes.</summary>
    private readonly ProxyNode _editing;

    private int _validatedPort;

    /// <summary>
    /// Initializes a new instance of the <see cref="EditNodeDialog"/> class with the
    /// node whose values are pre-filled into the form.
    /// </summary>
    /// <param name="node">The node to edit; its fields are copied so the original is untouched until the caller applies <see cref="Node"/>.</param>
    public EditNodeDialog(ProxyNode node)
    {
        ArgumentNullException.ThrowIfNull(node);

        InitializeComponent();

        _editing = new ProxyNode
        {
            Id = node.Id,
            Name = node.Name,
            Type = node.Type,
            Address = node.Address,
            Port = node.Port,
            PingMs = node.PingMs,
            IsSelected = node.IsSelected,
            RawConfig = node.RawConfig,
            ExtraParams = CloneExtraParams(node.ExtraParams),
        };

        NameBox.Text = _editing.Name ?? string.Empty;
        AddressBox.Text = _editing.Address ?? string.Empty;
        PortBox.Text = _editing.Port > 0 ? _editing.Port.ToString() : string.Empty;

        // Keep any protocol the node already carries even if it is not one of the
        // six known types, so closing without changing the type never loses it.
        var types = new List<string>(KnownTypes);
        if (!string.IsNullOrEmpty(_editing.Type) && !types.Contains(_editing.Type))
        {
            types.Add(_editing.Type);
        }

        TypeBox.ItemsSource = types;
        TypeBox.SelectedItem = _editing.Type;

        ExtraParamsBox.Text = SerializeExtraParams(_editing.ExtraParams);
    }

    /// <summary>The edited node; the caller reads this after the dialog closes with the primary button.</summary>
    public ProxyNode Node => _editing;

    private void OnPrimaryButtonClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        string? error = Validate();
        if (error is not null)
        {
            args.Cancel = true;
            ValidationText.Text = error;
            ValidationText.Visibility = Visibility.Visible;
            return;
        }

        _editing.Name = NameBox.Text.Trim();
        _editing.Type = TypeBox.SelectedItem as string;
        _editing.Address = AddressBox.Text.Trim();
        _editing.Port = _validatedPort;
        _editing.ExtraParams = ParseExtraParams(ExtraParamsBox.Text);
    }

    /// <summary>Returns a human-readable validation error, or null when every field is valid.</summary>
    private string? Validate()
    {
        if (string.IsNullOrWhiteSpace(NameBox.Text))
        {
            return "名称不能为空";
        }

        if (string.IsNullOrWhiteSpace(AddressBox.Text))
        {
            return "地址不能为空";
        }

        if (!int.TryParse(PortBox.Text.Trim(), out int port) || port is < 1 or > 65535)
        {
            return "端口必须是 1 - 65535 之间的整数";
        }

        _validatedPort = port;

        if (!TryParseExtraParams(ExtraParamsBox.Text, out _))
        {
            return "高级参数必须是有效的 JSON 对象";
        }

        return null;
    }

    /// <summary>
    /// Serializes the ExtraParams dictionary to indented JSON (empty when there are
    /// no parameters) using <see cref="JsonObject"/>, which needs no reflection.
    /// </summary>
    private static string SerializeExtraParams(Dictionary<string, JsonNode>? extra)
    {
        if (extra is null || extra.Count == 0)
        {
            return string.Empty;
        }

        var obj = new JsonObject();
        foreach (var pair in extra)
        {
            obj[pair.Key] = CopyNode(pair.Value);
        }

        return obj.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    /// <summary>Parses the ExtraParams text back into a dictionary; null when the text is blank.</summary>
    private static Dictionary<string, JsonNode>? ParseExtraParams(string text)
        => TryParseExtraParams(text, out var parsed) ? parsed : null;

    /// <summary>True when the text is blank or a valid JSON object; the parsed dictionary is returned via <paramref name="dict"/>.</summary>
    private static bool TryParseExtraParams(string text, out Dictionary<string, JsonNode>? dict)
    {
        dict = null;
        if (string.IsNullOrWhiteSpace(text))
        {
            return true;
        }

        JsonNode? root;
        try
        {
            root = JsonNode.Parse(text);
        }
        catch (JsonException)
        {
            return false;
        }

        if (root is not JsonObject obj)
        {
            return false;
        }

        var parsed = new Dictionary<string, JsonNode>();
        foreach (var pair in obj)
        {
            parsed[pair.Key] = pair.Value is null
                ? JsonValue.Create((object?)null)!
                : pair.Value;
        }

        dict = parsed;
        return true;
    }

    /// <summary>Deep-copies the dictionary so edits never alias the source node's tree.</summary>
    private static Dictionary<string, JsonNode>? CloneExtraParams(Dictionary<string, JsonNode>? extra)
    {
        if (extra is null)
        {
            return null;
        }

        var copy = new Dictionary<string, JsonNode>();
        foreach (var pair in extra)
        {
            copy[pair.Key] = CopyNode(pair.Value);
        }

        return copy;
    }

    /// <summary>Clones a node value, preserving explicit JSON nulls as a null <see cref="JsonValue"/>.</summary>
    private static JsonNode CopyNode(JsonNode? value)
        => value is null ? JsonValue.Create((object?)null)! : value.DeepClone();
}
