using HidSharp;

namespace SaitekMultiPanel.Hid;

/// <summary>Raw HID access to one Multi Panel: input report reading and LCD/LED feature writes.</summary>
public sealed class MultiPanelHid : IDisposable
{
    public const int VendorId = 0x06A3;
    public const int ProductId = 0x0D06;

    private readonly HidStream _stream;
    private readonly object _writeLock = new();

    private MultiPanelHid(HidDevice device, HidStream stream)
    {
        Device = device;
        _stream = stream;
        _stream.ReadTimeout = Timeout.Infinite;
    }

    public HidDevice Device { get; }

    public static IEnumerable<HidDevice> Enumerate() =>
        DeviceList.Local.GetHidDevices(VendorId, ProductId);

    public static MultiPanelHid Open(HidDevice device) => new(device, device.Open());

    /// <summary>Blocks until the next input report arrives. Returns the 3 payload bytes.</summary>
    public MultiPanelReport Read()
    {
        // Report is prefixed by report id 0.
        var buffer = new byte[Math.Max(Device.GetMaxInputReportLength(), 4)];
        var n = _stream.Read(buffer, 0, buffer.Length);
        if (n < 4)
            throw new IOException($"Short input report ({n} bytes)");
        return MultiPanelReport.FromBytes(buffer.AsSpan(1, 3));
    }

    /// <summary>Sends the 12-byte feature report (report id 0 + 5 upper digits + 5 lower digits + LED byte).</summary>
    public void WriteFeature(byte[] report)
    {
        // Windows requires the buffer to match the descriptor's feature length (13), so zero-pad.
        var buffer = new byte[Math.Max(Device.GetMaxFeatureReportLength(), report.Length)];
        report.CopyTo(buffer, 0);
        lock (_writeLock)
            _stream.SetFeature(buffer);
    }

    public void Dispose() => _stream.Dispose();
}
