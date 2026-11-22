using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Threading;
using Capsule.Settings;

namespace Capsule.Core;

internal enum HealthKind { CpuLoad, GpuLoad, GpuHot, Throttling, Memory, Disk }

internal sealed record HealthAlert(HealthKind Kind, string Title, string Status);

/// <summary>
/// Samples CPU, GPU, memory, heat and disk space every 5 seconds on a background thread using only
/// Windows' own counters (no drivers, no admin rights) and raises an alert when a limit is held.
/// </summary>
internal sealed class SystemMonitor : IDisposable
{
    // Every 5 s is plenty for "has this been high for 30 s?" and costs next to nothing.
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(5);

    private readonly Dispatcher _ui;
    private readonly Func<AppSettings> _settings;
    private readonly Timer _timer;
    private readonly object _sampleGate = new();
    private bool _disposed;
    private readonly SustainedAlert _cpu = new(TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(15));
    private readonly SustainedAlert _gpu = new(TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(30));
    private readonly SustainedAlert _gpuHot = new(TimeSpan.FromSeconds(10), TimeSpan.FromMinutes(15));
    private readonly SustainedAlert _throttle = new(TimeSpan.FromSeconds(10), TimeSpan.FromMinutes(15));
    private readonly SustainedAlert _memory = new(TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(20));
    private readonly Dictionary<string, DateTime> _diskWarned = new(StringComparer.OrdinalIgnoreCase);
    private readonly Pdh _pdh = new();
    private long _idle, _kernel, _user;
    private DateTime _nextDiskCheck = DateTime.UtcNow.AddSeconds(30);
    private int _busy;
    private int _ticks;
    private (double Percent, int TopPid)? _lastGpu;

    public SystemMonitor(Dispatcher ui, Func<AppSettings> settings)
    {
        _ui = ui;
        _settings = settings;
        _timer = new Timer(_ => Tick(), null, Timeout.Infinite, Timeout.Infinite);
    }

    public event Action<HealthAlert>? Alert;

    public void Start() => _timer.Change(Interval, Interval);

    public void Pause() => _timer.Change(Timeout.Infinite, Timeout.Infinite);

    public void Dispose()
    {
        _timer.Dispose();
        lock (_sampleGate)
        {
            _disposed = true; // a sample already running finishes first; none starts after this
            _pdh.Dispose();
        }
    }

    private void Tick()
    {
        if (Interlocked.Exchange(ref _busy, 1) == 1) return; // never overlap samples
        Monitor.Enter(_sampleGate);
        try
        {
            if (_disposed) return;
            var s = _settings();
            var now = DateTime.UtcNow;
            if (!s.HudEnabled || !(s.HudCpuGpu || s.HudHeat || s.HudMemoryDisk)) return;

            // Only measure what an alert that is switched on needs.
            var cpu = s.HudCpuGpu ? CpuPercent() : 0;
            // The GPU counter lists hundreds of engines, so read it only every third sample (15 s)
            // and only when GPU alerts are on. That still catches "busy for 30 s".
            if (s.HudCpuGpu && _ticks++ % 3 == 0) _lastGpu = _pdh.GpuLoad();
            var gpu = s.HudCpuGpu ? _lastGpu : null;
            var gpuTemp = s.HudHeat ? GpuTemperature.Max() : null;
            var limit = s.HudHeat ? _pdh.ThermalLimitPercent() : null;
            var memory = s.HudMemoryDisk ? MemoryPercent() : null;

            if (s.HudCpuGpu && _cpu.Update(cpu >= s.CpuAlertPercent, now))
            {
                Raise(new HealthAlert(HealthKind.CpuLoad, $"CPU {cpu:0}%", TopProcess.ByCpu() ?? "Working hard"));
            }

            if (s.HudCpuGpu && gpu is { } g && _gpu.Update(g.Percent >= s.GpuAlertPercent, now))
            {
                Raise(new HealthAlert(HealthKind.GpuLoad, $"GPU {g.Percent:0}%", TopProcess.NameOf(g.TopPid) ?? "Working hard"));
            }

            if (s.HudHeat && gpuTemp is { } t && _gpuHot.Update(t >= s.GpuTempAlertC, now))
            {
                Raise(new HealthAlert(HealthKind.GpuHot, $"GPU {t:0} °C", "Running hot"));
            }

            if (s.HudHeat && limit is { } l && _throttle.Update(l < 90, now))
            {
                Raise(new HealthAlert(HealthKind.Throttling, "CPU is slowing down", $"Slowed by heat · {l:0}% speed"));
            }

            if (s.HudMemoryDisk && memory is { } m && _memory.Update(m >= s.MemoryAlertPercent, now))
            {
                Raise(new HealthAlert(HealthKind.Memory, $"Memory {m}% used", TopProcess.ByMemory() ?? "Close some apps"));
            }

            if (s.HudMemoryDisk && now >= _nextDiskCheck)
            {
                _nextDiskCheck = now.AddMinutes(5);
                CheckDisks(s, now);
            }
        }
        catch (Exception ex)
        {
            Log.Error("System check failed", ex);
        }
        finally
        {
            Monitor.Exit(_sampleGate);
            Volatile.Write(ref _busy, 0);
        }
    }

    private void CheckDisks(AppSettings s, DateTime now)
    {
        foreach (var drive in DriveInfo.GetDrives())
        {
            try
            {
                if (drive.DriveType != DriveType.Fixed || !drive.IsReady) continue;
                var freeGb = drive.AvailableFreeSpace / 1_073_741_824.0;
                var low = freeGb < s.DiskFreeAlertGb && drive.TotalSize > 32L * 1_073_741_824;
                if (!low)
                {
                    _diskWarned.Remove(drive.Name);
                    continue;
                }

                if (_diskWarned.TryGetValue(drive.Name, out var last) && now - last < TimeSpan.FromHours(6)) continue;
                _diskWarned[drive.Name] = now;
                var label = DisplayText.Clean(drive.VolumeLabel, 40);
                var name = label.Length > 0 ? $"{label} ({drive.Name.TrimEnd('\\')})" : $"Drive {drive.Name.TrimEnd('\\')}";
                Raise(new HealthAlert(HealthKind.Disk, name, $"Only {freeGb:0.0} GB free"));
            }
            catch
            {
                // Drive went away or is locked.
            }
        }
    }

    private void Raise(HealthAlert alert)
    {
        Log.Info($"Alert: {alert.Title}");
        _ui.BeginInvoke(() => Alert?.Invoke(alert));
    }

    private double CpuPercent()
    {
        if (!GetSystemTimes(out var idle, out var kernel, out var user)) return 0;
        var idleDelta = idle - _idle;
        var total = kernel - _kernel + (user - _user); // kernel time includes idle time
        _idle = idle;
        _kernel = kernel;
        _user = user;
        return total <= 0 ? 0 : Math.Clamp(100.0 * (total - idleDelta) / total, 0, 100);
    }

    private static int? MemoryPercent()
    {
        var status = new MemoryStatus { Length = (uint)Marshal.SizeOf<MemoryStatus>() };
        return GlobalMemoryStatusEx(ref status) ? (int)status.MemoryLoad : null;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatus
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhys, AvailPhys, TotalPageFile, AvailPageFile, TotalVirtual, AvailVirtual, AvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatus status);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetSystemTimes(out long idle, out long kernel, out long user);
}

/// <summary>Finds which program is using the most CPU, memory or GPU (best effort; protected processes are skipped).</summary>
internal static class TopProcess
{
    public static string? ByCpu()
    {
        var first = Snapshot();
        Thread.Sleep(1000);
        var second = Snapshot();
        var best = second
            .Where(p => first.ContainsKey(p.Key))
            .Select(p => (p.Value.Name, Delta: p.Value.Cpu - first[p.Key].Cpu))
            .OrderByDescending(p => p.Delta)
            .FirstOrDefault();
        return best.Name is null ? null : Friendly(best.Name);
    }

    public static string? ByMemory()
    {
        string? name = null;
        long most = 0;
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                try
                {
                    if (process.Id <= 4 || process.WorkingSet64 <= most) continue;
                    most = process.WorkingSet64;
                    name = process.ProcessName;
                }
                catch
                {
                    // Gone or protected.
                }
            }
        }

        return name is null ? null : Friendly(name);
    }

    public static string? NameOf(int pid)
    {
        if (pid <= 4) return null;
        try
        {
            using var process = Process.GetProcessById(pid);
            return Friendly(process.ProcessName);
        }
        catch
        {
            return null;
        }
    }

    private static Dictionary<int, (string Name, TimeSpan Cpu)> Snapshot()
    {
        var result = new Dictionary<int, (string, TimeSpan)>();
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                try
                {
                    if (process.Id <= 4) continue;
                    result[process.Id] = (process.ProcessName, process.TotalProcessorTime);
                }
                catch
                {
                    // Access denied for protected processes.
                }
            }
        }

        return result;
    }

    private static string Friendly(string processName) => DisplayText.Clean(processName, 40) + ".exe";
}

/// <summary>Performance counters read through PDH (the same source Task Manager uses).</summary>
internal sealed class Pdh : IDisposable
{
    private const uint FormatDouble = 0x00000200;
    private const uint FormatNoCap100 = 0x00008000;
    private const int MoreData = unchecked((int)0x800007D2);

    private readonly IntPtr _query;
    private readonly IntPtr _heatQuery;
    private readonly IntPtr _gpu;
    private readonly IntPtr _thermal;
    private readonly bool _ok;

    public Pdh()
    {
        try
        {
            _ok = PdhOpenQueryW(null, IntPtr.Zero, out _query) == 0;
            if (!_ok) return;
            PdhAddEnglishCounterW(_query, @"\GPU Engine(*engtype_3D)\Utilization Percentage", IntPtr.Zero, out _gpu);
            PdhCollectQueryData(_query);
            if (PdhOpenQueryW(null, IntPtr.Zero, out _heatQuery) == 0)
            {
                PdhAddEnglishCounterW(_heatQuery, @"\Thermal Zone Information(*)\% Passive Limit", IntPtr.Zero, out _thermal);
                PdhCollectQueryData(_heatQuery);
            }
        }
        catch (Exception ex)
        {
            Log.Error("Performance counters are unavailable", ex);
            _ok = false;
        }
    }

    /// <summary>Total 3D engine use (capped at 100) and the process using the most.</summary>
    public (double Percent, int TopPid)? GpuLoad()
    {
        if (!_ok || _gpu == IntPtr.Zero || PdhCollectQueryData(_query) != 0) return null;
        var items = Read(_gpu);
        if (items is null) return null;

        var perProcess = new Dictionary<int, double>();
        foreach (var (name, value) in items)
        {
            // Instance names look like "pid_1234_luid_..._engtype_3D".
            if (!name.StartsWith("pid_", StringComparison.Ordinal)) continue;
            var end = name.IndexOf('_', 4);
            if (end < 0 || !int.TryParse(name.AsSpan(4, end - 4), out var pid)) continue;
            perProcess[pid] = perProcess.GetValueOrDefault(pid) + value;
        }

        if (perProcess.Count == 0) return (0, 0);
        var top = perProcess.MaxBy(p => p.Value);
        return (Math.Min(100, perProcess.Values.Sum()), top.Key);
    }

    /// <summary>Lowest "% passive limit" across thermal zones: 100 = full speed, lower = slowed down by heat.</summary>
    public double? ThermalLimitPercent()
    {
        if (!_ok || _thermal == IntPtr.Zero || PdhCollectQueryData(_heatQuery) != 0) return null;
        var items = Read(_thermal);
        return items is { Count: > 0 } ? items.Min(i => i.Value) : null;
    }

    public void Dispose()
    {
        if (!_ok) return;
        PdhCloseQuery(_query);
        if (_heatQuery != IntPtr.Zero) PdhCloseQuery(_heatQuery);
    }

    private static List<(string Name, double Value)>? Read(IntPtr counter)
    {
        uint size = 0;
        var status = PdhGetFormattedCounterArrayW(counter, FormatDouble | FormatNoCap100, ref size, out _, IntPtr.Zero);
        if (status != MoreData || size == 0 || size > 4 * 1024 * 1024) return null;

        var buffer = Marshal.AllocHGlobal((int)size);
        try
        {
            if (PdhGetFormattedCounterArrayW(counter, FormatDouble | FormatNoCap100, ref size, out var count, buffer) != 0) return null;
            var list = new List<(string, double)>((int)count);
            var itemSize = Marshal.SizeOf<Item>();
            for (var i = 0; i < count; i++)
            {
                var item = Marshal.PtrToStructure<Item>(buffer + i * itemSize);
                if (item.Value.Status != 0) continue;
                list.Add((Marshal.PtrToStringUni(item.Name) ?? "", item.Value.Value));
            }

            return list;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FormattedValue
    {
        public uint Status;
        public double Value;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Item
    {
        public IntPtr Name;
        public FormattedValue Value;
    }

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    private static extern int PdhOpenQueryW(string? source, IntPtr userData, out IntPtr query);

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    private static extern int PdhAddEnglishCounterW(IntPtr query, string path, IntPtr userData, out IntPtr counter);

    [DllImport("pdh.dll")]
    private static extern int PdhCollectQueryData(IntPtr query);

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    private static extern int PdhGetFormattedCounterArrayW(IntPtr counter, uint format, ref uint size, out uint count, IntPtr buffer);

    [DllImport("pdh.dll")]
    private static extern int PdhCloseQuery(IntPtr query);
}

/// <summary>GPU temperature from the Windows display driver interface (works for any vendor that reports it).</summary>
internal static class GpuTemperature
{
    private const int AdapterPerfData = 62; // KMTQAITYPE_ADAPTERPERFDATA

    public static double? Max()
    {
        var enumeration = new EnumAdapters2();
        if (D3DKMTEnumAdapters2(ref enumeration) != 0 || enumeration.Count == 0 || enumeration.Count > 64) return null;

        var infoSize = Marshal.SizeOf<AdapterInfo>();
        enumeration.Adapters = Marshal.AllocHGlobal(infoSize * (int)enumeration.Count);
        try
        {
            if (D3DKMTEnumAdapters2(ref enumeration) != 0) return null;
            double? hottest = null;
            for (var i = 0; i < enumeration.Count; i++)
            {
                var adapter = Marshal.PtrToStructure<AdapterInfo>(enumeration.Adapters + i * infoSize);
                var perfSize = Marshal.SizeOf<PerfData>();
                var perf = Marshal.AllocHGlobal(perfSize);
                try
                {
                    Marshal.StructureToPtr(new PerfData(), perf, false);
                    var query = new QueryAdapterInfo { Adapter = adapter.Handle, Type = AdapterPerfData, Data = perf, Size = (uint)perfSize };
                    if (D3DKMTQueryAdapterInfo(ref query) == 0)
                    {
                        var data = Marshal.PtrToStructure<PerfData>(perf);
                        var celsius = data.Temperature / 10.0; // tenths of a degree
                        if (celsius is > 5 and < 150 && (hottest is null || celsius > hottest)) hottest = celsius;
                    }
                }
                finally
                {
                    Marshal.FreeHGlobal(perf);
                    var close = new CloseAdapter { Adapter = adapter.Handle };
                    D3DKMTCloseAdapter(ref close);
                }
            }

            return hottest;
        }
        catch
        {
            return null;
        }
        finally
        {
            Marshal.FreeHGlobal(enumeration.Adapters);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Luid
    {
        public uint Low;
        public int High;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct AdapterInfo
    {
        public uint Handle;
        public Luid AdapterLuid;
        public uint NumOfSources;
        public int PrecisePresentRegionsPreferred;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct EnumAdapters2
    {
        public uint Count;
        public IntPtr Adapters;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct QueryAdapterInfo
    {
        public uint Adapter;
        public int Type;
        public IntPtr Data;
        public uint Size;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PerfData
    {
        public uint PhysicalAdapterIndex;
        public ulong MemoryFrequency;
        public ulong MaxMemoryFrequency;
        public ulong MaxMemoryFrequencyOC;
        public ulong MemoryBandwidth;
        public ulong PcieBandwidth;
        public uint FanRpm;
        public uint Power;
        public uint Temperature;
        public byte PowerStateOverride;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CloseAdapter
    {
        public uint Adapter;
    }

    [DllImport("gdi32.dll")]
    private static extern int D3DKMTEnumAdapters2(ref EnumAdapters2 enumeration);

    [DllImport("gdi32.dll")]
    private static extern int D3DKMTQueryAdapterInfo(ref QueryAdapterInfo query);

    [DllImport("gdi32.dll")]
    private static extern int D3DKMTCloseAdapter(ref CloseAdapter close);
}
