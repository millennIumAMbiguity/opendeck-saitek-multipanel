using System.Globalization;
using System.Text.Json.Nodes;
using SaitekMultiPanel.Hid;

namespace SaitekMultiPanel;

/// <summary>Shared rules for dial actions that show a value on the panel LCD.</summary>
public static class PanelDial
{
    /// <summary>Selector position a dial belongs to, if it's one of the panel's 5 knob dials.</summary>
    public static SelectorMode? ModeFor(string device, int position) =>
        device.StartsWith(DeviceManager.Namespace + "-") && position is >= 0 and <= 4 ? (SelectorMode)position : null;

    public static DisplayRow DefaultRow(SelectorMode? mode) => mode == SelectorMode.Vs ? DisplayRow.Lower : DisplayRow.Upper;

    /// <summary>The "row" setting only applies to ALT/VS; IAS/HDG/CRS only light the upper row.</summary>
    public static DisplayRow ResolveRow(JsonObject settings, SelectorMode? mode)
    {
        if (mode is SelectorMode.Ias or SelectorMode.Hdg or SelectorMode.Crs)
            return DisplayRow.Upper;
        var row = (settings["row"] as JsonValue)?.TryGetValue<string>(out var r) == true ? r : null;
        return row switch
        {
            "upper" => DisplayRow.Upper,
            "lower" => DisplayRow.Lower,
            _ => DefaultRow(mode),
        };
    }

    /// <summary>ALT and VS share the two-row display, so a value on either dial shows in both positions.</summary>
    public static bool IsShown(SelectorMode dialMode, SelectorMode selector) =>
        dialMode == selector || (IsAltVs(dialMode) && IsAltVs(selector));

    public static void Show(DisplayStore display, SelectorMode? mode, DisplayRow row, string? text)
    {
        if (mode is not { } m)
            return;
        if (IsAltVs(m))
        {
            display.SetRow(SelectorMode.Alt, row, text);
            display.SetRow(SelectorMode.Vs, row, text);
        }
        else
        {
            display.SetRow(m, row, text);
        }
    }

    private static bool IsAltVs(SelectorMode m) => m is SelectorMode.Alt or SelectorMode.Vs;

    public static long? ReadLong(JsonObject obj, string key) => obj[key] switch
    {
        JsonValue v when v.TryGetValue<long>(out var l) => l,
        JsonValue v when v.TryGetValue<double>(out var d) => (long)Math.Round(d),
        JsonValue v when v.TryGetValue<string>(out var s) && long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var p) => p,
        _ => null,
    };
}
