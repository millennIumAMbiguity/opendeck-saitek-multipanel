using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using OpenDeck.Sdk;
using SaitekMultiPanel.Hid;

namespace SaitekMultiPanel;

/// <summary>
/// "Counter" dial action: the knob changes a number that is shown on the panel LCD
/// (and as the dial's title in OpenDeck). Defaults depend on which dial it sits on,
/// e.g. ALT steps by 100 ft, HDG wraps 0-359.
/// </summary>
public sealed class CounterActions
{
    public const string ActionUuid = "io.github.millenniumambiguity.saitekmultipanel.counter";

    /// <summary>Ticks closer together than this count as "turning fast" and use the fast step.</summary>
    private static readonly TimeSpan FastThreshold = TimeSpan.FromMilliseconds(40);

    private readonly OpenDeckConnection _deck;
    private readonly DisplayStore _display;
    private readonly ConcurrentDictionary<string, Counter> _counters = new();

    public CounterActions(OpenDeckConnection deck, DisplayStore display)
    {
        _deck = deck;
        _display = display;
    }

    private sealed class Counter
    {
        public required string Context;
        public required string Device;
        public required int Position;
        public CounterConfig Config = null!;
        public JsonObject Settings = [];
        public long Value;
        public DateTime LastTick;
        public readonly object Lock = new();

        public SelectorMode? Mode => PanelDial.ModeFor(Device, Position);
    }

    public async Task OnEventAsync(string name, JsonElement message)
    {
        if (!message.TryGetProperty("action", out var a) || a.GetString() != ActionUuid)
            return;

        var context = message.GetProperty("context").GetString()!;
        var payload = message.GetProperty("payload");

        switch (name)
        {
            case "willAppear":
            {
                var counter = new Counter
                {
                    Context = context,
                    Device = message.GetProperty("device").GetString()!,
                    Position = payload.GetProperty("coordinates").GetProperty("column").GetInt32(),
                };
                ApplySettings(counter, payload.GetProperty("settings"));
                _counters[context] = counter;
                await PublishAsync(counter);
                break;
            }
            case "willDisappear":
                if (_counters.TryRemove(context, out var gone))
                    ShowOnPanel(gone, null);
                break;
            case "didReceiveSettings":
                if (_counters.TryGetValue(context, out var changed))
                {
                    ShowOnPanel(changed, null); // Row may have changed.
                    ApplySettings(changed, payload.GetProperty("settings"));
                    await PublishAsync(changed);
                }
                break;
            case "dialRotate":
                if (_counters.TryGetValue(context, out var turned))
                {
                    var ticks = payload.GetProperty("ticks").GetInt32();
                    lock (turned.Lock)
                    {
                        var now = DateTime.UtcNow;
                        var step = now - turned.LastTick < FastThreshold ? turned.Config.FastStep : turned.Config.Step;
                        turned.LastTick = now;
                        turned.Value = turned.Config.Apply(turned.Value + ticks * step);
                    }
                    await PublishAsync(turned, save: true);
                }
                break;
        }
    }

    private static void ApplySettings(Counter counter, JsonElement settings)
    {
        var obj = settings.ValueKind == JsonValueKind.Object ? JsonNode.Parse(settings.GetRawText())!.AsObject() : [];
        lock (counter.Lock)
        {
            counter.Settings = obj;
            counter.Config = CounterConfig.From(obj, counter.Mode);
            counter.Value = counter.Config.Apply(PanelDial.ReadLong(obj, "value") ?? counter.Value);
        }
    }

    private async Task PublishAsync(Counter counter, bool save = false)
    {
        long value;
        CounterConfig config;
        JsonObject? settings = null;
        lock (counter.Lock)
        {
            value = counter.Value;
            config = counter.Config;
            if (save)
            {
                counter.Settings["value"] = value;
                settings = counter.Settings.DeepClone().AsObject();
            }
        }

        var text = config.Format(value);
        ShowOnPanel(counter, text);
        await _deck.SetTitleAsync(counter.Context, text);
        if (settings is not null)
            await _deck.SetSettingsAsync(counter.Context, settings);
    }

    private void ShowOnPanel(Counter counter, string? text) =>
        PanelDial.Show(_display, counter.Mode, counter.Config.Row, text);
}

/// <summary>Counter settings with per-dial defaults. Empty/missing settings fall back to the default.</summary>
public sealed record CounterConfig(long Step, long FastStep, long Min, long Max, bool Wrap, int Digits, DisplayRow Row)
{
    public static CounterConfig Default(SelectorMode? mode) => mode switch
    {
        SelectorMode.Alt => new(100, 1000, 0, 99999, false, 0, PanelDial.DefaultRow(mode)),
        SelectorMode.Vs => new(100, 500, -9999, 9999, false, 0, PanelDial.DefaultRow(mode)),
        SelectorMode.Ias => new(1, 10, 0, 999, false, 0, PanelDial.DefaultRow(mode)),
        SelectorMode.Hdg or SelectorMode.Crs => new(1, 10, 0, 359, true, 3, PanelDial.DefaultRow(mode)),
        _ => new(1, 10, -9999, 99999, false, 0, PanelDial.DefaultRow(mode)),
    };

    public static CounterConfig From(JsonObject s, SelectorMode? mode)
    {
        var d = Default(mode);
        var min = PanelDial.ReadLong(s, "min") ?? d.Min;
        var max = PanelDial.ReadLong(s, "max") ?? d.Max;
        if (max < min)
            (min, max) = (max, min);

        return new CounterConfig(
            Math.Max(1, PanelDial.ReadLong(s, "step") ?? d.Step),
            Math.Max(1, PanelDial.ReadLong(s, "fastStep") ?? d.FastStep),
            min,
            max,
            s["wrap"]?.GetValueKind() switch
            {
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                _ => d.Wrap,
            },
            (int)Math.Clamp(PanelDial.ReadLong(s, "digits") ?? d.Digits, 0, 5),
            PanelDial.ResolveRow(s, mode));
    }

    public long Apply(long value)
    {
        if (!Wrap)
            return Math.Clamp(value, Min, Max);
        var range = Max - Min + 1;
        return Min + ((value - Min) % range + range) % range;
    }

    public string Format(long value) =>
        Digits > 0 && value >= 0
            ? value.ToString(new string('0', Digits), CultureInfo.InvariantCulture)
            : value.ToString(CultureInfo.InvariantCulture);
}
