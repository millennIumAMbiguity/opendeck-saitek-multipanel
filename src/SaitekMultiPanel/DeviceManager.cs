using System.Collections.Concurrent;
using System.Text.Json;
using HidSharp;
using OpenDeck.Sdk;
using SaitekMultiPanel.Hid;

namespace SaitekMultiPanel;

/// <summary>Finds Multi Panels, handles hot-plug, and routes OpenDeck device events to the right panel.</summary>
public sealed class DeviceManager : IDisposable
{
    /// <summary>Must match "DeviceNamespace" in manifest.json (OpenDeck matches on the first 2 chars of the id).</summary>
    public const string Namespace = "pz";

    private readonly OpenDeckConnection _deck;
    private readonly DisplayStore _display;
    private readonly ConcurrentDictionary<string, (PanelController Panel, CancellationTokenSource Cts)> _panels = new();
    private readonly SemaphoreSlim _scanLock = new(1, 1);
    private CancellationToken _ct;

    public DeviceManager(OpenDeckConnection deck, DisplayStore display)
    {
        _deck = deck;
        _display = display;
    }

    public IEnumerable<PanelController> Panels => _panels.Values.Select(p => p.Panel);

    /// <summary>Selector position changed on a panel (device id, new mode). None when the panel goes away.</summary>
    public event Action<string, SelectorMode>? PanelModeChanged;

    public SelectorMode GetMode(string id) =>
        _panels.TryGetValue(id, out var entry) ? entry.Panel.Mode : SelectorMode.None;

    public async Task StartAsync(CancellationToken ct)
    {
        _ct = ct;
        DeviceList.Local.Changed += OnDeviceListChanged;
        await ScanAsync();
    }

    private void OnDeviceListChanged(object? sender, DeviceListChangedEventArgs e) => _ = ScanAsync();

    private async Task ScanAsync()
    {
        await _scanLock.WaitAsync(_ct);
        try
        {
            var present = MultiPanelHid.Enumerate().ToList();
            var known = _panels.Values.Select(p => p.Panel.DevicePath).ToHashSet(StringComparer.OrdinalIgnoreCase);

            foreach (var device in present.Where(d => !known.Contains(d.DevicePath)))
                Attach(device);
        }
        catch (Exception ex)
        {
            Log.Error($"Device scan failed: {ex}");
        }
        finally
        {
            _scanLock.Release();
        }
    }

    private void Attach(HidDevice device)
    {
        MultiPanelHid hid;
        try
        {
            hid = MultiPanelHid.Open(device);
        }
        catch (Exception ex)
        {
            Log.Warn($"Cannot open {device.DevicePath}: {ex.Message}");
            return;
        }

        var id = MakeId(device);
        var cts = CancellationTokenSource.CreateLinkedTokenSource(_ct);
        var panel = new PanelController(id, hid, _deck, _display);
        panel.ModeChanged += p => PanelModeChanged?.Invoke(p.Id, p.Mode);
        _panels[id] = (panel, cts);
        _ = RunPanelAsync(panel, cts);
    }

    private async Task RunPanelAsync(PanelController panel, CancellationTokenSource cts)
    {
        try
        {
            await panel.RunAsync(cts.Token);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException)
        {
            Log.Info($"{panel.Id} disconnected ({ex.GetType().Name})");
        }
        catch (Exception ex)
        {
            Log.Error($"{panel.Id} crashed: {ex}");
        }

        _panels.TryRemove(panel.Id, out _);
        PanelModeChanged?.Invoke(panel.Id, SelectorMode.None);
        panel.Dispose();
        cts.Dispose();
        if (!_ct.IsCancellationRequested)
            await _deck.DeregisterDeviceAsync(panel.Id);
    }

    /// <summary>
    /// Stable id so OpenDeck profiles survive restarts. The PZ70 usually reports no serial,
    /// so fall back to an index (first panel = "pz-multipanel").
    /// </summary>
    private string MakeId(HidDevice device)
    {
        string? serial = null;
        try
        {
            serial = device.GetSerialNumber();
        }
        catch
        {
            // Not provided by this firmware.
        }

        if (!string.IsNullOrWhiteSpace(serial))
            return $"{Namespace}-{serial.Trim()}";

        for (var i = 0; ; i++)
        {
            var id = i == 0 ? $"{Namespace}-multipanel" : $"{Namespace}-multipanel-{i + 1}";
            if (!_panels.ContainsKey(id))
                return id;
        }
    }

    /// <summary>Handles inbound OpenDeck events addressed to our devices.</summary>
    public void OnEvent(string name, JsonElement message)
    {
        switch (name)
        {
            case "setImage":
            {
                if (!TryGetPanel(message, out var panel))
                    return;
                var controller = message.TryGetProperty("controller", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString() : null;
                if (controller == "Encoder")
                    return; // Dials have no screen.
                int? position = message.TryGetProperty("position", out var p) && p.ValueKind == JsonValueKind.Number ? p.GetInt32() : null;
                var image = message.TryGetProperty("image", out var i) && i.ValueKind == JsonValueKind.String ? i.GetString() : null;
                panel.OnKeyImage(position, image);
                break;
            }
            case "setBrightness":
            {
                if (TryGetPanel(message, out var panel) && message.TryGetProperty("brightness", out var b))
                    panel.SetBrightness(b.GetInt32());
                break;
            }
            case "systemDidWakeUp":
                foreach (var panel in Panels)
                {
                    panel.Refresh();
                    _ = _deck.RerenderImagesAsync(panel.Id);
                }
                break;
        }
    }

    private bool TryGetPanel(JsonElement message, out PanelController panel)
    {
        panel = null!;
        if (!message.TryGetProperty("device", out var d) || d.GetString() is not { } id)
            return false;
        if (!_panels.TryGetValue(id, out var entry))
            return false;
        panel = entry.Panel;
        return true;
    }

    public void Dispose()
    {
        DeviceList.Local.Changed -= OnDeviceListChanged;
        foreach (var (_, cts) in _panels.Values)
            cts.Cancel();
    }
}
