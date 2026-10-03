using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using OpenDeck.Sdk;
using SaitekMultiPanel.Hid;

namespace SaitekMultiPanel;

/// <summary>
/// "System Monitor" dial action: shows CPU/RAM/GPU stats on the panel LCD.
/// Sampling only runs while at least one monitor is actually on screen, i.e. the
/// panel's selector is on that dial's position. Otherwise there is no timer and no reads.
/// </summary>
public sealed class MonitorActions : IDisposable
{
    public const string ActionUuid = "io.github.millenniumambiguity.saitekmultipanel.monitor";
    private const int DefaultIntervalMs = 1000;
    private const int MinIntervalMs = 250;

    private readonly OpenDeckConnection _deck;
    private readonly DisplayStore _display;
    private readonly DeviceManager _devices;
    private readonly SystemMetrics _metrics = SystemMetrics.Create();
    private readonly ConcurrentDictionary<string, Monitor> _monitors = new();
    private readonly object _loopLock = new();
    private readonly object _metricsLock = new();
    private CancellationTokenSource? _loop;

    public MonitorActions(OpenDeckConnection deck, DisplayStore display, DeviceManager devices)
    {
        _deck = deck;
        _display = display;
        _devices = devices;
        _devices.PanelModeChanged += (_, _) => Reevaluate();
    }

    private sealed class Monitor
    {
        public required string Context;
        public required string Device;
        public required int Position;
        public Metric Metric;
        public int IntervalMs = DefaultIntervalMs;
        public DisplayRow Row;
        public string? LastText;

        public SelectorMode? Mode => PanelDial.ModeFor(Device, Position);
    }

    public Task OnEventAsync(string name, JsonElement message)
    {
        if (!message.TryGetProperty("action", out var a) || a.GetString() != ActionUuid)
            return Task.CompletedTask;

        var context = message.GetProperty("context").GetString()!;
        var payload = message.GetProperty("payload");

        switch (name)
        {
            case "willAppear":
            {
                var monitor = new Monitor
                {
                    Context = context,
                    Device = message.GetProperty("device").GetString()!,
                    Position = payload.GetProperty("coordinates").GetProperty("column").GetInt32(),
                };
                ApplySettings(monitor, payload.GetProperty("settings"));
                _monitors[context] = monitor;
                break;
            }
            case "willDisappear":
                if (_monitors.TryRemove(context, out var gone))
                    PanelDial.Show(_display, gone.Mode, gone.Row, null);
                break;
            case "didReceiveSettings":
                if (_monitors.TryGetValue(context, out var changed))
                {
                    PanelDial.Show(_display, changed.Mode, changed.Row, null);
                    ApplySettings(changed, payload.GetProperty("settings"));
                }
                break;
            default:
                return Task.CompletedTask;
        }

        Reevaluate();
        return Task.CompletedTask;
    }

    private static void ApplySettings(Monitor monitor, JsonElement settings)
    {
        var obj = settings.ValueKind == JsonValueKind.Object ? JsonNode.Parse(settings.GetRawText())!.AsObject() : [];
        monitor.Metric = (obj["metric"] as JsonValue)?.TryGetValue<string>(out var m) == true && Enum.TryParse<Metric>(m, true, out var metric)
            ? metric
            : Metric.CpuPercent;
        monitor.IntervalMs = (int)Math.Max(MinIntervalMs, PanelDial.ReadLong(obj, "interval") ?? DefaultIntervalMs);
        monitor.Row = PanelDial.ResolveRow(obj, monitor.Mode);
        monitor.LastText = null;
    }

    /// <summary>On the panel: shown when the selector is on this dial's position. Elsewhere: shown while placed.</summary>
    private bool IsShown(Monitor m) =>
        m.Mode is not { } mode || PanelDial.IsShown(mode, _devices.GetMode(m.Device));

    /// <summary>Starts the sampling loop when something is on screen, stops it when nothing is.</summary>
    private void Reevaluate()
    {
        var anyShown = _monitors.Values.Any(IsShown);
        lock (_loopLock)
        {
            if (anyShown && _loop is null)
            {
                _loop = new CancellationTokenSource();
                _ = RunLoopAsync(_loop.Token);
                Log.Info("Monitor sampling started");
            }
            else if (!anyShown && _loop is not null)
            {
                _loop.Cancel();
                _loop = null;
                Log.Info("Monitor sampling stopped");
            }
        }
    }

    private async Task RunLoopAsync(CancellationToken ct)
    {
        try
        {
            var primed = Needed(Shown());
            lock (_metricsLock)
                _metrics.Prime(primed);
            await Task.Delay(MinIntervalMs, ct);

            while (!ct.IsCancellationRequested)
            {
                var shown = Shown();
                if (shown.Count == 0)
                    break;

                var needed = Needed(shown);
                if (!needed.IsSubsetOf(primed))
                {
                    // A newly shown metric (e.g. CPU) needs a fresh baseline before its first real value.
                    lock (_metricsLock)
                        _metrics.Prime(needed);
                    await Task.Delay(MinIntervalMs, ct);
                }

                Dictionary<Metric, double?> values;
                lock (_metricsLock)
                    values = _metrics.Sample(needed);
                primed = needed;
                foreach (var m in shown)
                {
                    var text = values.GetValueOrDefault(m.Metric) is { } v
                        ? Math.Round(v).ToString(CultureInfo.InvariantCulture)
                        : null;
                    if (text == m.LastText)
                        continue;
                    m.LastText = text;
                    PanelDial.Show(_display, m.Mode, m.Row, text);
                    await _deck.SetTitleAsync(m.Context, text ?? "--");
                }

                await Task.Delay(shown.Min(m => m.IntervalMs), ct);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Log.Error($"Monitor loop failed: {ex}");
        }
        finally
        {
            lock (_loopLock)
            {
                if (_loop?.Token == ct)
                    _loop = null;
                if (_loop is null)
                    lock (_metricsLock)
                        _metrics.Release();
            }
        }
    }

    private List<Monitor> Shown() => _monitors.Values.Where(IsShown).ToList();

    private static HashSet<Metric> Needed(IEnumerable<Monitor> shown) => shown.Select(m => m.Metric).ToHashSet();

    public void Dispose()
    {
        lock (_loopLock)
        {
            _loop?.Cancel();
            _loop = null;
        }
        _metrics.Dispose();
    }
}
