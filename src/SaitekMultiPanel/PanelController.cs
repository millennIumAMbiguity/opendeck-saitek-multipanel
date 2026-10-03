using OpenDeck.Sdk;
using SaitekMultiPanel.Hid;

namespace SaitekMultiPanel;

/// <summary>Bridges one physical Multi Panel to OpenDeck: input -> key/dial events, key images + display store -> LCD/LEDs.</summary>
public sealed class PanelController : IDisposable
{
    private readonly MultiPanelHid _hid;
    private readonly OpenDeckConnection _deck;
    private readonly DisplayStore _display;
    private readonly object _outputLock = new();

    private MultiPanelReport? _last;
    private SelectorMode _mode = SelectorMode.None;
    private byte _imageLeds;
    private readonly int?[] _imageHashes = new int?[PanelLayout.LedKeyCount];
    private bool _dark;
    private byte[]? _written;

    public PanelController(string id, MultiPanelHid hid, OpenDeckConnection deck, DisplayStore display)
    {
        Id = id;
        _hid = hid;
        _deck = deck;
        _display = display;
        _display.Changed += OnDisplayChanged;
    }

    public string Id { get; }

    /// <summary>Raised when the selector knob moves (and once for the initial position).</summary>
    public event Action<PanelController>? ModeChanged;

    public SelectorMode Mode => _mode;

    public byte Leds
    {
        get
        {
            lock (_outputLock)
                return _display.Get(_mode).LedOverride ?? _imageLeds;
        }
    }

    public string DevicePath => _hid.Device.DevicePath;

    /// <summary>Registers with OpenDeck and pumps input reports until the device is unplugged or cancelled.</summary>
    public async Task RunAsync(CancellationToken ct)
    {
        await _deck.RegisterDeviceAsync(Id, "Saitek Pro Flight Multi Panel",
            PanelLayout.Rows, PanelLayout.Columns, PanelLayout.Encoders);
        Log.Info($"Registered {Id} ({DevicePath})");

        // OpenDeck can drop image updates while it draws a freshly loaded profile; ask once more
        // when it has settled so the button LEDs match the keys.
        _ = Task.Delay(TimeSpan.FromSeconds(3), ct).ContinueWith(t =>
        {
            if (!t.IsCanceled)
                _ = _deck.RerenderImagesAsync(Id);
        }, TaskScheduler.Default);

        await using var disposeOnCancel = ct.Register(_hid.Dispose);
        while (!ct.IsCancellationRequested)
        {
            var report = await Task.Run(_hid.Read, ct);
            await HandleReportAsync(report);
        }
    }

    private async Task HandleReportAsync(MultiPanelReport report)
    {
        Log.Debug($"{Id} report {report.Bits:X6}");

        if (_last is not { } prev)
        {
            // First report is the baseline; don't fire events for switches that are already set.
            _last = report;
            SetMode(report.Mode);
            return;
        }
        _last = report;

        if (report.Mode != prev.Mode && report.Mode != SelectorMode.None)
            SetMode(report.Mode);

        foreach (var (key, bit, inverted) in PanelLayout.Keys)
        {
            var was = prev[bit] ^ inverted;
            var now = report[bit] ^ inverted;
            if (was == now)
                continue;
            Log.Debug($"{Id} key {key} {(now ? "down" : "up")}");
            if (now)
                await _deck.KeyDownAsync(Id, key);
            else
                await _deck.KeyUpAsync(Id, key);
        }

        if (_mode != SelectorMode.None)
        {
            var dial = PanelLayout.EncoderFor(_mode);
            if (Rose(prev, report, MultiPanelReport.KnobInc))
                await TurnAsync(dial, 1);
            if (Rose(prev, report, MultiPanelReport.KnobDec))
                await TurnAsync(dial, -1);
        }

        if (Rose(prev, report, MultiPanelReport.TrimUp))
            await TurnAsync(PanelLayout.EncoderTrim, 1);
        if (Rose(prev, report, MultiPanelReport.TrimDown))
            await TurnAsync(PanelLayout.EncoderTrim, -1);
    }

    private static bool Rose(MultiPanelReport prev, MultiPanelReport now, int bit) => !prev[bit] && now[bit];

    private Task TurnAsync(int dial, short ticks)
    {
        Log.Debug($"{Id} dial {dial} {ticks:+0;-0}");
        return _deck.EncoderChangeAsync(Id, dial, ticks);
    }

    private void SetMode(SelectorMode mode)
    {
        _mode = mode;
        Log.Debug($"{Id} selector -> {mode}");
        // The panel only redraws its mode label (ALT/VS/IAS/HDG/CRS) after a feature write.
        UpdateOutput(force: true);
        ModeChanged?.Invoke(this);
    }

    /// <summary>OpenDeck setImage for a key. Position null = whole device cleared.</summary>
    public void OnKeyImage(int? position, string? image)
    {
        lock (_outputLock)
        {
            if (position is null)
            {
                _imageLeds = 0;
                Array.Clear(_imageHashes);
            }
            else if (position < PanelLayout.LedKeyCount)
            {
                // OpenDeck re-sends identical images often (profile loads, rerenders); skip decoding those.
                var hash = image?.GetHashCode();
                if (hash == _imageHashes[position.Value] && hash is not null)
                    return;
                _imageHashes[position.Value] = hash;
                var mask = (byte)(1 << position.Value);
                _imageLeds = LedImageAnalyzer.IsLit(image, position.Value) ? (byte)(_imageLeds | mask) : (byte)(_imageLeds & ~mask);
            }
        }
        UpdateOutput();
    }

    public void SetBrightness(int brightness)
    {
        _dark = brightness == 0;
        UpdateOutput();
    }

    /// <summary>After sleep/replug the panel loses its state.</summary>
    public void Refresh() => UpdateOutput(force: true);

    private void OnDisplayChanged() => UpdateOutput();

    private void UpdateOutput(bool force = false)
    {
        lock (_outputLock)
        {
            var (upper, lower, ledOverride) = _display.Get(_mode);
            var report = _dark
                ? PanelOutput.Build(null, null, 0)
                : PanelOutput.Build(upper, lower, ledOverride ?? _imageLeds);

            if (!force && _written is not null && report.AsSpan().SequenceEqual(_written))
                return;

            try
            {
                _hid.WriteFeature(report);
                _written = report;
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException)
            {
                Log.Warn($"{Id} feature write failed: {ex.Message}");
            }
        }
    }

    public void Dispose()
    {
        _display.Changed -= OnDisplayChanged;
        _hid.Dispose();
    }
}
