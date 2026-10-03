namespace SaitekMultiPanel.Hid;

/// <summary>Position of the 5-way mode selector on the left of the panel.</summary>
public enum SelectorMode { None = -1, Alt = 0, Vs = 1, Ias = 2, Hdg = 3, Crs = 4 }

/// <summary>
/// Decoded 3-byte input report of the Saitek/Logitech Pro Flight Multi Panel (PZ70, 06A3:0D06).
/// <code>
/// Byte 0: b0 SEL_ALT  b1 SEL_VS  b2 SEL_IAS  b3 SEL_HDG  b4 SEL_CRS  b5 KNOB_INC  b6 KNOB_DEC  b7 AP
/// Byte 1: b0 HDG  b1 NAV  b2 IAS  b3 ALT  b4 VS  b5 APR  b6 REV  b7 AUTO_THROTTLE (switch ARM)
/// Byte 2: b0 FLAPS_UP  b1 FLAPS_DOWN  b2 TRIM_DOWN  b3 TRIM_UP
/// </code>
/// </summary>
public readonly record struct MultiPanelReport(uint Bits)
{
    // Bit indexes into the 24-bit value (byte0 = bits 0-7, byte1 = 8-15, byte2 = 16-23).
    public const int SelAlt = 0, SelVs = 1, SelIas = 2, SelHdg = 3, SelCrs = 4;
    public const int KnobInc = 5, KnobDec = 6;
    public const int BtnAp = 7, BtnHdg = 8, BtnNav = 9, BtnIas = 10, BtnAlt = 11, BtnVs = 12, BtnApr = 13, BtnRev = 14;
    public const int AutoThrottle = 15;
    public const int FlapsUp = 16, FlapsDown = 17, TrimDown = 18, TrimUp = 19;

    public static MultiPanelReport FromBytes(ReadOnlySpan<byte> data) =>
        new((uint)(data[0] | data[1] << 8 | data[2] << 16));

    public bool this[int bit] => (Bits & (1u << bit)) != 0;

    public SelectorMode Mode =>
        this[SelAlt] ? SelectorMode.Alt :
        this[SelVs] ? SelectorMode.Vs :
        this[SelIas] ? SelectorMode.Ias :
        this[SelHdg] ? SelectorMode.Hdg :
        this[SelCrs] ? SelectorMode.Crs :
        SelectorMode.None;
}
