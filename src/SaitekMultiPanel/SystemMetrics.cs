using System.Runtime.InteropServices;

namespace SaitekMultiPanel;

public enum Metric
{
    CpuPercent,
    RamPercent,
    RamUsedMb,
    RamUsedGb,
    RamFreeMb,
    RamFreeGb,
    GpuPercent,
    GpuMemoryMb,
}

/// <summary>
/// Reads system stats from the OS. Nothing runs in the background: values are only read when
/// <see cref="Sample"/> is called, and OS handles (GPU queries) are closed by <see cref="Release"/>.
/// </summary>
public abstract class SystemMetrics : IDisposable
{
    public static SystemMetrics Create() =>
        OperatingSystem.IsWindows() ? new WindowsSystemMetrics() : new LinuxSystemMetrics();

    /// <summary>Takes a baseline for metrics that are computed from deltas (CPU, GPU load).</summary>
    public abstract void Prime(IReadOnlySet<Metric> needed);

    public abstract Dictionary<Metric, double?> Sample(IReadOnlySet<Metric> needed);

    /// <summary>Drops OS handles and baselines while nothing is shown.</summary>
    public abstract void Release();

    public void Dispose() => Release();

    protected static bool NeedsRam(IReadOnlySet<Metric> needed) =>
        needed.Overlaps([Metric.RamPercent, Metric.RamUsedMb, Metric.RamUsedGb, Metric.RamFreeMb, Metric.RamFreeGb]);

    protected static bool NeedsGpu(IReadOnlySet<Metric> needed) =>
        needed.Contains(Metric.GpuPercent) || needed.Contains(Metric.GpuMemoryMb);

    protected static void AddRam(Dictionary<Metric, double?> result, double totalBytes, double availableBytes)
    {
        const double mb = 1024 * 1024, gb = mb * 1024;
        var used = totalBytes - availableBytes;
        result[Metric.RamPercent] = totalBytes > 0 ? 100 * used / totalBytes : null;
        result[Metric.RamUsedMb] = used / mb;
        result[Metric.RamUsedGb] = used / gb;
        result[Metric.RamFreeMb] = availableBytes / mb;
        result[Metric.RamFreeGb] = availableBytes / gb;
    }

    /// <summary>CPU load from two (idle, total) time samples.</summary>
    protected static double? CpuPercent((ulong Idle, ulong Total)? last, (ulong Idle, ulong Total)? now)
    {
        if (now is not { } n || last is not { } l || n.Total <= l.Total)
            return null;
        return 100.0 * (1 - (double)(n.Idle - l.Idle) / (n.Total - l.Total));
    }
}

/// <summary>Windows: GetSystemTimes, GlobalMemoryStatusEx, PDH GPU counters (same as Task Manager).</summary>
public sealed class WindowsSystemMetrics : SystemMetrics
{
    private (ulong Idle, ulong Total)? _lastCpu;
    private PdhQuery? _gpu;

    public override void Prime(IReadOnlySet<Metric> needed)
    {
        if (needed.Contains(Metric.CpuPercent))
            _lastCpu = ReadCpuTimes();
        if (NeedsGpu(needed))
            Gpu().Collect();
    }

    public override Dictionary<Metric, double?> Sample(IReadOnlySet<Metric> needed)
    {
        var result = new Dictionary<Metric, double?>();

        if (needed.Contains(Metric.CpuPercent))
            result[Metric.CpuPercent] = ReadCpuPercent();

        if (NeedsRam(needed))
        {
            var mem = new MemoryStatusEx { Length = (uint)Marshal.SizeOf<MemoryStatusEx>() };
            if (GlobalMemoryStatusEx(ref mem))
                AddRam(result, mem.TotalPhys, mem.AvailPhys);
        }

        if (NeedsGpu(needed))
        {
            var gpu = Gpu();
            gpu.Collect();
            if (needed.Contains(Metric.GpuPercent))
                result[Metric.GpuPercent] = gpu.Utilization();
            if (needed.Contains(Metric.GpuMemoryMb))
                result[Metric.GpuMemoryMb] = gpu.DedicatedBytes() / (1024.0 * 1024);
        }

        return result;
    }

    public override void Release()
    {
        _gpu?.Dispose();
        _gpu = null;
        _lastCpu = null;
    }

    private PdhQuery Gpu() => _gpu ??= new PdhQuery();

    private double? ReadCpuPercent()
    {
        var now = ReadCpuTimes();
        var last = _lastCpu;
        _lastCpu = now;
        return CpuPercent(last, now);
    }

    private static (ulong Idle, ulong Total)? ReadCpuTimes()
    {
        if (!GetSystemTimes(out var idle, out var kernel, out var user))
            return null;
        // Kernel time includes idle time.
        return (idle, kernel + user);
    }

    /// <summary>GPU counters via PDH, the same source Task Manager uses.</summary>
    private sealed class PdhQuery : IDisposable
    {
        private const uint PdhFmtDouble = 0x00000200;
        private const uint PdhMoreData = 0x800007D2;

        private readonly IntPtr _query;
        private readonly IntPtr _engine;
        private readonly IntPtr _memory;

        public PdhQuery()
        {
            PdhOpenQueryW(null, IntPtr.Zero, out _query);
            PdhAddEnglishCounterW(_query, @"\GPU Engine(*)\Utilization Percentage", IntPtr.Zero, out _engine);
            PdhAddEnglishCounterW(_query, @"\GPU Adapter Memory(*)\Dedicated Usage", IntPtr.Zero, out _memory);
        }

        public void Collect() => PdhCollectQueryData(_query);

        /// <summary>Like Task Manager: sum per engine type (3D, Copy, VideoDecode...), report the busiest type.</summary>
        public double? Utilization()
        {
            var byType = new Dictionary<string, double>();
            foreach (var (name, value) in Read(_engine))
            {
                var i = name.IndexOf("engtype_", StringComparison.Ordinal);
                var type = i >= 0 ? name[(i + 8)..] : name;
                byType[type] = byType.GetValueOrDefault(type) + value;
            }
            return byType.Count == 0 ? null : Math.Min(100, byType.Values.Max());
        }

        public double? DedicatedBytes()
        {
            var values = Read(_memory);
            return values.Count == 0 ? null : values.Sum(v => v.Value);
        }

        private static List<(string Name, double Value)> Read(IntPtr counter)
        {
            var result = new List<(string, double)>();
            uint size = 0, count = 0;
            if (PdhGetFormattedCounterArrayW(counter, PdhFmtDouble, ref size, ref count, IntPtr.Zero) != PdhMoreData)
                return result;

            var buffer = Marshal.AllocHGlobal((int)size);
            try
            {
                if (PdhGetFormattedCounterArrayW(counter, PdhFmtDouble, ref size, ref count, buffer) != 0)
                    return result;
                var itemSize = Marshal.SizeOf<PdhFmtCounterValueItem>();
                for (var i = 0; i < count; i++)
                {
                    var item = Marshal.PtrToStructure<PdhFmtCounterValueItem>(buffer + i * itemSize);
                    if (item.CStatus == 0)
                        result.Add((Marshal.PtrToStringUni(item.Name) ?? "", item.DoubleValue));
                }
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
            return result;
        }

        public void Dispose() => PdhCloseQuery(_query);

        [StructLayout(LayoutKind.Sequential)]
        private struct PdhFmtCounterValueItem
        {
            public IntPtr Name;
            public uint CStatus;
            public double DoubleValue;
        }

        [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
        private static extern uint PdhOpenQueryW(string? dataSource, IntPtr userData, out IntPtr query);

        [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
        private static extern uint PdhAddEnglishCounterW(IntPtr query, string path, IntPtr userData, out IntPtr counter);

        [DllImport("pdh.dll")]
        private static extern uint PdhCollectQueryData(IntPtr query);

        [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
        private static extern uint PdhGetFormattedCounterArrayW(IntPtr counter, uint format, ref uint bufferSize, ref uint itemCount, IntPtr items);

        [DllImport("pdh.dll")]
        private static extern uint PdhCloseQuery(IntPtr query);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatusEx
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhys;
        public ulong AvailPhys;
        public ulong TotalPageFile;
        public ulong AvailPageFile;
        public ulong TotalVirtual;
        public ulong AvailVirtual;
        public ulong AvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetSystemTimes(out ulong idle, out ulong kernel, out ulong user);
}
