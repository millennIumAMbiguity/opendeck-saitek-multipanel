using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using SaitekMultiPanel.Hid;

namespace SaitekMultiPanel;

/// <summary>
/// Local HTTP API so other programs/plugins (e.g. a War Thunder plugin) can drive the LCD and LEDs.
/// <code>
/// GET  /state                     -> {"devices":[{"id":"pz-multipanel","mode":"alt","leds":0}]}
/// POST /display {"upper":"12000","lower":"-500"}               all selector positions
/// POST /display {"mode":"ias","upper":"250"}                   one selector position
/// POST /display {"leds":5}  / {"leds":null}                     LED override / back to key images
/// POST /dial {"dial":0,"text":"12500","row":"upper"}            like a built-in dial action on knob dial 0-4
///                                                              (ALT/VS share the display; row optional; text null clears)
/// GET  /watch                     -> newline-delimited JSON, one line per selector change:
///                                    {"device":"pz-multipanel","mode":"alt"}   (current state sent on connect)
/// POST /clear
/// </code>
/// </summary>
public sealed class DisplayApiServer : IDisposable
{
    public const int DefaultPort = 47070;

    private readonly HttpListener _listener = new();
    private readonly DisplayStore _display;
    private readonly DeviceManager _devices;
    private readonly List<StreamWriter> _watchers = [];
    private readonly SemaphoreSlim _watchWrite = new(1, 1);

    public DisplayApiServer(DisplayStore display, DeviceManager devices, int port)
    {
        _display = display;
        _devices = devices;
        _devices.PanelModeChanged += (id, mode) => _ = BroadcastAsync(ModeLine(id, mode));
        _listener.Prefixes.Add($"http://localhost:{port}/");
        _listener.Prefixes.Add($"http://127.0.0.1:{port}/");
    }

    public async Task RunAsync(CancellationToken ct)
    {
        try
        {
            _listener.Start();
        }
        catch (HttpListenerException ex)
        {
            Log.Error($"Display API could not start: {ex.Message}");
            return;
        }

        Log.Info($"Display API listening on {string.Join(", ", _listener.Prefixes)}");
        await using var stopOnCancel = ct.Register(_listener.Stop);

        while (!ct.IsCancellationRequested)
        {
            HttpListenerContext context;
            try
            {
                context = await _listener.GetContextAsync();
            }
            catch (Exception) when (ct.IsCancellationRequested)
            {
                break;
            }

            _ = HandleAsync(context);
        }
    }

    private async Task HandleAsync(HttpListenerContext context)
    {
        var request = context.Request;
        var response = context.Response;
        try
        {
            var path = request.Url?.AbsolutePath.TrimEnd('/').ToLowerInvariant();
            switch (request.HttpMethod, path)
            {
                case ("GET", "/state"):
                    await WriteJsonAsync(response, 200, State());
                    break;
                case ("POST", "/display"):
                    using (var reader = new StreamReader(request.InputStream, Encoding.UTF8))
                        ApplyDisplay(JsonNode.Parse(await reader.ReadToEndAsync())?.AsObject() ?? []);
                    await WriteJsonAsync(response, 200, State());
                    break;
                case ("POST", "/dial"):
                    using (var reader = new StreamReader(request.InputStream, Encoding.UTF8))
                        ApplyDial(JsonNode.Parse(await reader.ReadToEndAsync())?.AsObject() ?? []);
                    await WriteJsonAsync(response, 200, State());
                    break;
                case ("GET", "/watch"):
                    await WatchAsync(response);
                    break;
                case ("POST", "/clear"):
                    _display.Clear();
                    await WriteJsonAsync(response, 200, State());
                    break;
                default:
                    await WriteJsonAsync(response, 404, new JsonObject { ["error"] = "not found" });
                    break;
            }
        }
        catch (Exception ex) when (ex is JsonException or FormatException or ArgumentException or InvalidOperationException)
        {
            await WriteJsonAsync(response, 400, new JsonObject { ["error"] = ex.Message });
        }
        catch (Exception ex)
        {
            Log.Warn($"Display API error: {ex.Message}");
        }
    }

    private void ApplyDial(JsonObject body)
    {
        var dial = (int)(PanelDial.ReadLong(body, "dial") ?? throw new ArgumentException("dial (0-4) is required"));
        if (dial is < 0 or > 4)
            throw new ArgumentException("dial must be 0-4 (ALT, VS, IAS, HDG, CRS)");
        var mode = (SelectorMode)dial;
        PanelDial.Show(_display, mode, PanelDial.ResolveRow(body, mode), AsText(body["text"]));
    }

    /// <summary>Keeps the response open and streams selector changes, so clients don't have to poll.</summary>
    private async Task WatchAsync(HttpListenerResponse response)
    {
        response.StatusCode = 200;
        response.ContentType = "application/x-ndjson";
        response.SendChunked = true;
        var writer = new StreamWriter(response.OutputStream, new UTF8Encoding(false)) { AutoFlush = true };
        lock (_watchers)
            _watchers.Add(writer);
        foreach (var panel in _devices.Panels)
            await WriteLineAsync(writer, ModeLine(panel.Id, panel.Mode));
    }

    private static string ModeLine(string id, SelectorMode mode) =>
        new JsonObject { ["device"] = id, ["mode"] = mode.ToString().ToLowerInvariant() }.ToJsonString();

    private async Task BroadcastAsync(string line)
    {
        StreamWriter[] writers;
        lock (_watchers)
            writers = [.. _watchers];
        foreach (var writer in writers)
            await WriteLineAsync(writer, line);
    }

    private async Task WriteLineAsync(StreamWriter writer, string line)
    {
        await _watchWrite.WaitAsync();
        try
        {
            await writer.WriteLineAsync(line);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or HttpListenerException)
        {
            lock (_watchers)
                _watchers.Remove(writer);
            writer.Dispose();
        }
        finally
        {
            _watchWrite.Release();
        }
    }

    private void ApplyDisplay(JsonObject body)
    {
        SelectorMode? mode = body["mode"]?.GetValue<string>() is { } m
            ? Enum.Parse<SelectorMode>(m, ignoreCase: true)
            : null;

        if (body.ContainsKey("upper") || body.ContainsKey("lower"))
            _display.SetText(mode, AsText(body["upper"]), AsText(body["lower"]));

        if (body.ContainsKey("leds"))
            _display.SetLedOverride(body["leds"] is { } leds ? (byte)leds.GetValue<int>() : null);
    }

    /// <summary>Accepts numbers or strings, so both {"upper":250} and {"upper":"250"} work.</summary>
    private static string? AsText(JsonNode? node) => node?.GetValueKind() switch
    {
        null or JsonValueKind.Null => null,
        JsonValueKind.Number => Math.Round(node.GetValue<double>()).ToString(System.Globalization.CultureInfo.InvariantCulture),
        _ => node.GetValue<string>(),
    };

    private JsonObject State() => new()
    {
        ["devices"] = new JsonArray(_devices.Panels.Select(p => (JsonNode)new JsonObject
        {
            ["id"] = p.Id,
            ["mode"] = p.Mode.ToString().ToLowerInvariant(),
            ["leds"] = p.Leds,
        }).ToArray()),
    };

    private static async Task WriteJsonAsync(HttpListenerResponse response, int status, JsonNode body)
    {
        var bytes = Encoding.UTF8.GetBytes(body.ToJsonString());
        response.StatusCode = status;
        response.ContentType = "application/json";
        response.ContentLength64 = bytes.Length;
        await response.OutputStream.WriteAsync(bytes);
        response.Close();
    }

    public void Dispose() => ((IDisposable)_listener).Dispose();
}
