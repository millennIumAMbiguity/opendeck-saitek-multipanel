namespace SaitekMultiPanel.Hid;

/// <summary>Builds the feature report that drives the two 5-digit LCD rows and the 8 button LEDs.</summary>
public static class PanelOutput
{
    private const byte Blank = 0x0F; // any value >= 0x0A darkens the digit
    private const byte Dash = 0xEE;  // only rendered on the lower row

    public static byte[] Build(string? upper, string? lower, byte leds)
    {
        var report = new byte[12];
        Encode(upper, report.AsSpan(1, 5), allowDash: false);
        Encode(lower, report.AsSpan(6, 5), allowDash: true);
        report[11] = leds;
        return report;
    }

    /// <summary>
    /// Right-aligns <paramref name="text"/> into 5 digits. Supports 0-9, space and '-'.
    /// Values that do not fit are shown as all dashes (lower row) or blank (upper row).
    /// </summary>
    private static void Encode(string? text, Span<byte> digits, bool allowDash)
    {
        digits.Fill(Blank);
        if (string.IsNullOrEmpty(text))
            return;

        if (text.Length > digits.Length)
        {
            digits.Fill(allowDash ? Dash : Blank);
            return;
        }

        var offset = digits.Length - text.Length;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            digits[offset + i] = c switch
            {
                >= '0' and <= '9' => (byte)(c - '0'),
                '-' when allowDash => Dash,
                _ => Blank,
            };
        }
    }
}
