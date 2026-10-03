using SaitekMultiPanel.Hid;

namespace SaitekMultiPanel;

/// <summary>How the physical panel is exposed to OpenDeck.</summary>
public static class PanelLayout
{
    // Keys, 2 rows x 6 columns. Positions 0-7 match the LED bit order of the feature report.
    public const byte Rows = 2;
    public const byte Columns = 6;

    public const int KeyAp = 0, KeyHdg = 1, KeyNav = 2, KeyIas = 3, KeyAlt = 4, KeyVs = 5;
    public const int KeyApr = 6, KeyRev = 7;
    public const int KeyAutoThrottleOn = 8, KeyAutoThrottleOff = 9;
    public const int KeyFlapsUp = 10, KeyFlapsDown = 11;
    public const int LedKeyCount = 8;

    /// <summary>Key position -> report bit. Inverted entries are "held" while the bit is clear.</summary>
    public static readonly (int Key, int Bit, bool Inverted)[] Keys =
    [
        (KeyAp, MultiPanelReport.BtnAp, false),
        (KeyHdg, MultiPanelReport.BtnHdg, false),
        (KeyNav, MultiPanelReport.BtnNav, false),
        (KeyIas, MultiPanelReport.BtnIas, false),
        (KeyAlt, MultiPanelReport.BtnAlt, false),
        (KeyVs, MultiPanelReport.BtnVs, false),
        (KeyApr, MultiPanelReport.BtnApr, false),
        (KeyRev, MultiPanelReport.BtnRev, false),
        (KeyAutoThrottleOn, MultiPanelReport.AutoThrottle, false),
        (KeyAutoThrottleOff, MultiPanelReport.AutoThrottle, true),
        (KeyFlapsUp, MultiPanelReport.FlapsUp, false),
        (KeyFlapsDown, MultiPanelReport.FlapsDown, false),
    ];

    // Dials. The big knob drives the dial matching the selector position (index == (int)SelectorMode).
    public const byte Encoders = 6;
    public const int EncoderTrim = 5;

    public static int EncoderFor(SelectorMode mode) => (int)mode;
}
