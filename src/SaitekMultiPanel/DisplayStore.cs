using SaitekMultiPanel.Hid;

namespace SaitekMultiPanel;

public enum DisplayRow { Upper = 0, Lower = 1 }

/// <summary>
/// What the two LCD rows should show for each selector position, plus an optional LED override.
/// Written by counter actions and the local display API, read by every panel.
/// Per-mode text wins over the all-modes default, row by row.
/// </summary>
public sealed class DisplayStore
{
    private const int Modes = 5;

    private readonly object _lock = new();
    private readonly string?[,] _byMode = new string?[Modes, 2];
    private readonly string?[] _default = new string?[2];
    private byte? _ledOverride;

    public event Action? Changed;

    /// <summary>Set both rows for one selector position, or for all positions when <paramref name="mode"/> is null.</summary>
    public void SetText(SelectorMode? mode, string? upper, string? lower)
    {
        lock (_lock)
        {
            if (mode is { } m)
            {
                _byMode[(int)m, 0] = upper;
                _byMode[(int)m, 1] = lower;
            }
            else
            {
                Array.Clear(_byMode);
                _default[0] = upper;
                _default[1] = lower;
            }
        }
        Changed?.Invoke();
    }

    /// <summary>Set a single row for one selector position, leaving the other row alone.</summary>
    public void SetRow(SelectorMode mode, DisplayRow row, string? text)
    {
        lock (_lock)
            _byMode[(int)mode, (int)row] = text;
        Changed?.Invoke();
    }

    /// <summary>Force LED mask (bit 0 = AP ... bit 7 = REV). Null returns control to OpenDeck key images.</summary>
    public void SetLedOverride(byte? leds)
    {
        lock (_lock)
            _ledOverride = leds;
        Changed?.Invoke();
    }

    public void Clear()
    {
        lock (_lock)
        {
            Array.Clear(_byMode);
            Array.Clear(_default);
            _ledOverride = null;
        }
        Changed?.Invoke();
    }

    public (string? Upper, string? Lower, byte? LedOverride) Get(SelectorMode mode)
    {
        lock (_lock)
        {
            if (mode == SelectorMode.None)
                return (_default[0], _default[1], _ledOverride);
            var m = (int)mode;
            return (_byMode[m, 0] ?? _default[0], _byMode[m, 1] ?? _default[1], _ledOverride);
        }
    }
}
