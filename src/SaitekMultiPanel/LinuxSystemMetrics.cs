using System.Globalization;
using System.Runtime.InteropServices;

namespace SaitekMultiPanel;

/// <summary>
/// Linux: /proc/stat and /proc/meminfo for CPU/RAM. GPU from amdgpu sysfs
/// (gpu_busy_percent, mem_info_vram_used) or NVIDIA's NVML library when present.
/// </summary>
public sealed class LinuxSystemMetrics : SystemMetrics
{
    private (ulong Idle, ulong Total)? _lastCpu;
    private Nvml? _nvml;
    private bool _nvmlTried;

    public override void Prime(IReadOnlySet<Metric> needed)
    {
        if (needed.Contains(Metric.CpuPercent))
            _lastCpu = ReadCpuTimes();
    }

    public override Dictionary<Metric, double?> Sample(IReadOnlySet<Metric> needed)
    {
        var result = new Dictionary<Metric, double?>();

        if (needed.Contains(Metric.CpuPercent))
        {
            var now = ReadCpuTimes();
            result[Metric.CpuPercent] = CpuPercent(_lastCpu, now);
            _lastCpu = now;
        }

        if (NeedsRam(needed) && ReadMemInfo() is { } mem)
            AddRam(result, mem.Total, mem.Available);

        if (NeedsGpu(needed))
        {
            var (load, vram) = ReadAmdGpu();
            if (load is null && vram is null && Nvidia() is { } nvml)
                (load, vram) = nvml.Read();
            result[Metric.GpuPercent] = load;
            result[Metric.GpuMemoryMb] = vram / (1024.0 * 1024);
        }

        return result;
    }

    public override void Release()
    {
        _lastCpu = null;
        _nvml?.Dispose();
        _nvml = null;
        _nvmlTried = false;
    }

    /// <summary>First line of /proc/stat: "cpu user nice system idle iowait irq softirq steal ...".</summary>
    private static (ulong Idle, ulong Total)? ReadCpuTimes()
    {
        try
        {
            using var reader = new StreamReader("/proc/stat");
            var parts = reader.ReadLine()?.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts is null || parts.Length < 8 || parts[0] != "cpu")
                return null;
            var v = parts.Skip(1).Take(8).Select(p => ulong.Parse(p, CultureInfo.InvariantCulture)).ToArray();
            return (v[3] + v[4], v.Aggregate(0UL, (a, b) => a + b));
        }
        catch (Exception ex) when (ex is IOException or FormatException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static (double Total, double Available)? ReadMemInfo()
    {
        try
        {
            double? total = null, available = null;
            foreach (var line in File.ReadLines("/proc/meminfo"))
            {
                if (line.StartsWith("MemTotal:"))
                    total = Kb(line);
                else if (line.StartsWith("MemAvailable:"))
                    available = Kb(line);
                if (total is not null && available is not null)
                    return (total.Value, available.Value);
            }
            return null;
        }
        catch (Exception ex) when (ex is IOException or FormatException or UnauthorizedAccessException)
        {
            return null;
        }

        static double Kb(string line) =>
            1024 * double.Parse(line.Split(' ', StringSplitOptions.RemoveEmptyEntries)[1], CultureInfo.InvariantCulture);
    }

    /// <summary>AMD (amdgpu driver): busiest card's load, total VRAM used across cards.</summary>
    private static (double? Load, double? VramBytes) ReadAmdGpu()
    {
        double? load = null, vram = null;
        try
        {
            foreach (var card in Directory.EnumerateDirectories("/sys/class/drm", "card*"))
            {
                var device = Path.Combine(card, "device");
                if (ReadNumber(Path.Combine(device, "gpu_busy_percent")) is { } busy)
                    load = Math.Max(load ?? 0, busy);
                if (ReadNumber(Path.Combine(device, "mem_info_vram_used")) is { } used)
                    vram = (vram ?? 0) + used;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
        return (load, vram);
    }

    private static double? ReadNumber(string path)
    {
        try
        {
            return File.Exists(path) && double.TryParse(File.ReadAllText(path).Trim(), CultureInfo.InvariantCulture, out var v) ? v : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private Nvml? Nvidia()
    {
        if (!_nvmlTried)
        {
            _nvmlTried = true;
            _nvml = Nvml.TryOpen();
        }
        return _nvml;
    }

    /// <summary>Minimal NVML binding (ships with the NVIDIA driver as libnvidia-ml.so.1).</summary>
    private sealed class Nvml : IDisposable
    {
        private const string Lib = "libnvidia-ml.so.1";

        private Nvml()
        {
        }

        public static Nvml? TryOpen()
        {
            try
            {
                return nvmlInit_v2() == 0 ? new Nvml() : null;
            }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
            {
                return null;
            }
        }

        public (double? Load, double? VramBytes) Read()
        {
            if (nvmlDeviceGetCount_v2(out var count) != 0 || count == 0)
                return (null, null);
            double? load = null, vram = null;
            for (uint i = 0; i < count; i++)
            {
                if (nvmlDeviceGetHandleByIndex_v2(i, out var device) != 0)
                    continue;
                if (nvmlDeviceGetUtilizationRates(device, out var util) == 0)
                    load = Math.Max(load ?? 0, util.Gpu);
                if (nvmlDeviceGetMemoryInfo(device, out var mem) == 0)
                    vram = (vram ?? 0) + mem.Used;
            }
            return (load, vram);
        }

        public void Dispose() => nvmlShutdown();

        [StructLayout(LayoutKind.Sequential)]
        private struct Utilization
        {
            public uint Gpu;
            public uint Memory;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct Memory
        {
            public ulong Total;
            public ulong Free;
            public ulong Used;
        }

        [DllImport(Lib)] private static extern int nvmlInit_v2();
        [DllImport(Lib)] private static extern int nvmlShutdown();
        [DllImport(Lib)] private static extern int nvmlDeviceGetCount_v2(out uint count);
        [DllImport(Lib)] private static extern int nvmlDeviceGetHandleByIndex_v2(uint index, out IntPtr device);
        [DllImport(Lib)] private static extern int nvmlDeviceGetUtilizationRates(IntPtr device, out Utilization utilization);
        [DllImport(Lib)] private static extern int nvmlDeviceGetMemoryInfo(IntPtr device, out Memory memory);
    }
}
