using OpenDeck.Sdk;
using SaitekMultiPanel;
using SaitekMultiPanel.Hid;

if (args.Contains("--diag"))
    return Diagnostics.Run();
if (args.Contains("--metrics"))
    return Diagnostics.Metrics();
if (args.Contains("--sweep"))
    return Diagnostics.Sweep();

PluginArgs pluginArgs;
try
{
    pluginArgs = PluginArgs.Parse(args);
}
catch (ArgumentException ex)
{
    Console.Error.WriteLine(ex.Message);
    Console.Error.WriteLine("Run with --diag to test the panel without OpenDeck.");
    return 1;
}

Log.Info($"Starting {pluginArgs.PluginUuid}, OpenDeck port {pluginArgs.Port}");

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
};

var display = new DisplayStore();
await using var deck = new OpenDeckConnection(pluginArgs);
using var devices = new DeviceManager(deck, display);
var counters = new CounterActions(deck, display);
using var monitors = new MonitorActions(deck, display, devices);
deck.EventHandler = async (name, message) =>
{
    Log.Debug($"<- {name} {message.GetRawText()[..Math.Min(160, message.GetRawText().Length)]}");
    try
    {
        devices.OnEvent(name, message);
        await counters.OnEventAsync(name, message);
        await monitors.OnEventAsync(name, message);
    }
    catch (Exception ex)
    {
        Log.Error($"Handling {name} failed: {ex}");
    }
};

await deck.ConnectAsync(cts.Token);
await devices.StartAsync(cts.Token);

var apiPort = int.TryParse(Environment.GetEnvironmentVariable("MULTIPANEL_API_PORT"), out var p) ? p : DisplayApiServer.DefaultPort;
using var api = new DisplayApiServer(display, devices, apiPort);
var apiTask = api.RunAsync(cts.Token);

try
{
    // Runs until OpenDeck closes the socket (OpenDeck quitting or reloading the plugin).
    await deck.RunAsync(cts.Token);
}
catch (Exception ex) when (ex is OperationCanceledException or System.Net.WebSockets.WebSocketException)
{
    Log.Info($"Connection ended: {ex.Message}");
}

Log.Info("Shutting down");
cts.Cancel();
await apiTask;
return 0;

/// <summary>Standalone hardware test: prints decoded input and exercises the LCD/LEDs.</summary>
static class Diagnostics
{
    /// <summary>
    /// Shows every undocumented digit code (0x0A-0xFF) on the lower row, 5 per page, to look for
    /// extra symbols. Upper row = page number. Knob (selector on ALT) flips pages.
    /// </summary>
    public static int Sweep()
    {
        var device = MultiPanelHid.Enumerate().FirstOrDefault();
        if (device is null)
        {
            Console.WriteLine("No Multi Panel found.");
            return 1;
        }

        using var hid = MultiPanelHid.Open(device);
        var codes = Enumerable.Range(0x0A, 0x100 - 0x0A).Select(c => (byte)c).ToArray();
        var pages = (codes.Length + 4) / 5;
        var page = 0;

        void Show()
        {
            var report = PanelOutput.Build((page + 1).ToString(), null, 0);
            var pageCodes = codes.Skip(page * 5).Take(5).ToArray();
            for (var i = 0; i < 5; i++)
                report[6 + i] = i < pageCodes.Length ? pageCodes[i] : (byte)0x0F;
            hid.WriteFeature(report);
            Console.WriteLine($"page {page + 1,2}/{pages}: lower row codes (left to right) {string.Join(" ", pageCodes.Select(c => $"0x{c:X2}"))}");
        }

        Console.WriteLine("Set the selector to ALT. Turn the knob to change page. Ctrl+C to quit.");
        Show();
        MultiPanelReport? last = null;
        while (true)
        {
            var report = hid.Read();
            if (last is { } prev)
            {
                var step = (!prev[MultiPanelReport.KnobInc] && report[MultiPanelReport.KnobInc] ? 1 : 0)
                         - (!prev[MultiPanelReport.KnobDec] && report[MultiPanelReport.KnobDec] ? 1 : 0);
                if (step != 0 || report.Mode != prev.Mode)
                {
                    page = Math.Clamp(page + step, 0, pages - 1);
                    Show();
                }
            }
            last = report;
        }
    }

    /// <summary>Prints every system metric a few times, to check the readings against Task Manager.</summary>
    public static int Metrics()
    {
        using var metrics = SystemMetrics.Create();
        var all = Enum.GetValues<Metric>().ToHashSet();
        metrics.Prime(all);
        for (var i = 0; i < 3; i++)
        {
            Thread.Sleep(1000);
            Console.WriteLine(string.Join("  ", metrics.Sample(all).Select(kv => $"{kv.Key}={kv.Value:0.#}")));
        }
        return 0;
    }

    public static int Run()
    {
        var device = MultiPanelHid.Enumerate().FirstOrDefault();
        if (device is null)
        {
            Console.WriteLine("No Multi Panel (06A3:0D06) found.");
            return 1;
        }

        Console.WriteLine($"Found {device.DevicePath}");
        Console.WriteLine($"Input report length {device.GetMaxInputReportLength()}, feature length {device.GetMaxFeatureReportLength()}");
        using var hid = MultiPanelHid.Open(device);

        hid.WriteFeature(PanelOutput.Build("12345", "-6789", 0b0101_0101));
        Console.WriteLine("Display should show 12345 / -6789 (ALT mode) with every other LED on. Ctrl+C to quit.");

        MultiPanelReport? last = null;
        var ticks = 0;
        while (true)
        {
            var report = hid.Read();
            if (last is { } prev)
            {
                if (!prev[MultiPanelReport.KnobInc] && report[MultiPanelReport.KnobInc]) ticks++;
                if (!prev[MultiPanelReport.KnobDec] && report[MultiPanelReport.KnobDec]) ticks--;
            }
            if (report.Mode != last?.Mode)
                hid.WriteFeature(PanelOutput.Build(ticks.ToString(), (-ticks).ToString(), (byte)(1 << Math.Max(0, (int)report.Mode))));
            else if (report[MultiPanelReport.KnobInc] || report[MultiPanelReport.KnobDec])
                hid.WriteFeature(PanelOutput.Build(Math.Abs(ticks).ToString(), ticks.ToString(), 0));

            Console.WriteLine($"{Convert.ToString(report.Bits, 2).PadLeft(24, '0')}  mode={report.Mode,-4} ticks={ticks}");
            last = report;
        }
    }
}
