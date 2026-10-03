using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace OpenDeck.Sdk;

/// <summary>
/// WebSocket connection to OpenDeck. Speaks the Stream Deck plugin protocol plus
/// OpenDeck's device-plugin extensions (registerDevice, keyDown, encoderChange, ...).
/// </summary>
public sealed class OpenDeckConnection : IAsyncDisposable
{
    private readonly PluginArgs _args;
    private readonly ClientWebSocket _socket = new();
    private readonly SemaphoreSlim _sendLock = new(1, 1);

    public OpenDeckConnection(PluginArgs args) => _args = args;

    /// <summary>Called for every inbound event, one at a time in arrival order. Args: event name, full message.</summary>
    public Func<string, JsonElement, Task>? EventHandler { get; set; }

    public async Task ConnectAsync(CancellationToken ct)
    {
        await _socket.ConnectAsync(new Uri($"ws://127.0.0.1:{_args.Port}"), ct);
        await SendAsync(new JsonObject { ["event"] = _args.RegisterEvent, ["uuid"] = _args.PluginUuid }, ct);
    }

    /// <summary>Reads messages until the socket closes or <paramref name="ct"/> fires.</summary>
    public async Task RunAsync(CancellationToken ct)
    {
        var buffer = new byte[64 * 1024];
        using var message = new MemoryStream();

        while (_socket.State == WebSocketState.Open && !ct.IsCancellationRequested)
        {
            var result = await _socket.ReceiveAsync(buffer, ct);
            if (result.MessageType == WebSocketMessageType.Close)
                break;

            message.Write(buffer, 0, result.Count);
            if (!result.EndOfMessage)
                continue;

            try
            {
                using var doc = JsonDocument.Parse(message.ToArray());
                var root = doc.RootElement;
                if (root.TryGetProperty("event", out var ev) && ev.GetString() is { } name && EventHandler is { } handler)
                    await handler(name, root.Clone());
            }
            catch (JsonException ex)
            {
                Console.Error.WriteLine($"Bad message from OpenDeck: {ex.Message}");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Console.Error.WriteLine($"Event handler failed: {ex}");
            }

            message.SetLength(0);
        }
    }

    public async Task SendAsync(JsonNode message, CancellationToken ct = default)
    {
        var bytes = Encoding.UTF8.GetBytes(message.ToJsonString());
        await _sendLock.WaitAsync(ct);
        try
        {
            if (_socket.State == WebSocketState.Open)
                await _socket.SendAsync(bytes, WebSocketMessageType.Text, true, ct);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    private Task Send(string ev, JsonNode payload) =>
        SendAsync(new JsonObject { ["event"] = ev, ["payload"] = payload });

    // ---- Device plugin API (see OpenDeck src-tauri/src/events/inbound/devices.rs) ----

    /// <summary>Device id must start with the 2-char DeviceNamespace from the manifest.</summary>
    public Task RegisterDeviceAsync(string id, string name, byte rows, byte columns, byte encoders, byte type = 0) =>
        Send("registerDevice", new JsonObject
        {
            ["id"] = id,
            ["name"] = name,
            ["rows"] = rows,
            ["columns"] = columns,
            ["encoders"] = encoders,
            ["type"] = type,
        });

    public Task DeregisterDeviceAsync(string id) => Send("deregisterDevice", id);

    public Task RerenderImagesAsync(string id) => Send("rerenderImages", id);

    public Task KeyDownAsync(string device, int position) => Send("keyDown", Press(device, position));

    public Task KeyUpAsync(string device, int position) => Send("keyUp", Press(device, position));

    public Task EncoderDownAsync(string device, int position) => Send("encoderDown", Press(device, position));

    public Task EncoderUpAsync(string device, int position) => Send("encoderUp", Press(device, position));

    public Task EncoderChangeAsync(string device, int position, short ticks) =>
        Send("encoderChange", new JsonObject { ["device"] = device, ["position"] = position, ["ticks"] = ticks });

    public Task LogMessageAsync(string message) => Send("logMessage", new JsonObject { ["message"] = message });

    // ---- Action API ----

    public Task SetSettingsAsync(string context, JsonObject settings) =>
        SendAsync(new JsonObject { ["event"] = "setSettings", ["context"] = context, ["payload"] = settings });

    /// <summary>Image as a data URL (png/jpeg/svg). Null restores the manifest image.</summary>
    public Task SetImageAsync(string context, string? image) =>
        SendAsync(new JsonObject { ["event"] = "setImage", ["context"] = context, ["payload"] = new JsonObject { ["image"] = image } });

    public Task SetTitleAsync(string context, string? title) =>
        SendAsync(new JsonObject { ["event"] = "setTitle", ["context"] = context, ["payload"] = new JsonObject { ["title"] = title } });

    private static JsonObject Press(string device, int position) =>
        new() { ["device"] = device, ["position"] = position };

    public async ValueTask DisposeAsync()
    {
        if (_socket.State == WebSocketState.Open)
        {
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(1));
                await _socket.CloseAsync(WebSocketCloseStatus.NormalClosure, null, cts.Token);
            }
            catch
            {
                // Shutting down anyway.
            }
        }
        _socket.Dispose();
        _sendLock.Dispose();
    }
}
