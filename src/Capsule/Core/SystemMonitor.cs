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

    // Busy CPU or GPU: the average over 30 s must reach the limit. GPU is read every 15 s, CPU every 5 s.
    private readonly AverageAlert _cpu = new(TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(15), TimeSpan.FromSeconds(20));
    private readonly AverageAlert _gpu = new(TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(30), TimeSpan.FromSeconds(45));
    private readonly SustainedAlert _gpuHot = new(TimeSpan.FromSeconds(10), TimeSpan.FromMinutes(15));
    private readonly SustainedAlert _throttle = new(TimeSpan.FromSeconds(10), TimeSpan.FromMinutes(15));
    private readonly SustainedAlert _memory = new(TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(20));
    private readonly Dictionary<string, DateTime> _diskWarned = new(StringComparer.OrdinalIgnoreCase);
    private readonly Pdh _pdh = new();
    private bool _disposed;
    private long _idle, _kernel, _user;
    private DateTime _nextDiskCheck = DateTime.UtcNow.AddSeconds(30);
    private int _busy;
    private int _ticks;

    public SystemMonitor(Dispatcher ui, Func<AppSettings> settings)
    {
        _ui = ui;
        _settings = settings;
        _timer = new Timer(_ => Tick(), null, Timeout.Infinite, Timeout.Infinite);
    }

    public event Action<HealthAlert>? Alert;

    public void Start() => _timer.Change(Interval, Interval);

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
            var gpuTemp = s.HudHeat ? GpuTemperature.Max() : null;
            var limit = s.HudHeat ? _pdh.ThermalLimitPercent() : null;
            var memory = s.HudMemoryDisk ? MemoryPercent() : null;

            if (s.HudCpuGpu)
            {
                // The same "CPU utility" figure Task Manager shows; the plain processor time if that counter is missing.
                var cpu = _pdh.CpuUtility() ?? CpuPercent();
                if (_cpu.Update(cpu, s.CpuAlertPercent, now) is { } average)
                {
                    var app = Busiest.Describe(Busiest.App(ProcessList.CpuUse(TimeSpan.FromSeconds(1))), "Several apps are busy");
                    Raise(new HealthAlert(HealthKind.CpuLoad, $"CPU {average:0}%", app));
                }

                // The GPU counters list many engines, so they're read every third sample (15 s). That still catches
                // "busy for 30 s".
                if (_ticks++ % 3 == 0 && _pdh.GpuLoad() is { } gpu && _gpu.Update(gpu.Percent, s.GpuAlertPercent, now) is { } gpuAverage)
                {
                    var names = ProcessList.Names();
                    var users = gpu.Users.Select(u => (names.GetValueOrDefault(u.Pid, ""), u.Value));
                    Raise(new HealthAlert(HealthKind.GpuLoad, $"GPU {gpuAverage:0}%", Busiest.Describe(Busiest.App(users), "Several apps are busy")));
                }
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
                Raise(new HealthAlert(HealthKind.Memory, $"Memory {m}% used", Busiest.Describe(Busiest.App(ProcessList.MemoryUse()), "Close some apps")));
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

    // Processor time from GetSystemTimes, used only when the "CPU utility" counter can't be read.
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

/// <summary>
/// Every running process with its name, CPU time and memory, read in one call from Windows' own process list (what
/// Task Manager uses). Unlike opening each process, this also covers protected and elevated programs, so a busy game
/// with anti-cheat or a system service is never skipped and the blame put on whatever comes next.
/// </summary>
internal static class ProcessList
{
    private const int SystemProcessInformation = 5;
    private const int InfoLengthMismatch = unchecked((int)0xC0000004);
    private const int MaxBufferBytes = 64 * 1024 * 1024;

    public readonly record struct Entry(int Pid, string Name, long CpuTime, long WorkingSet);

    /// <summary>CPU time each process used during <paramref name="window"/>, by program name.</summary>
    public static List<(string Name, double Amount)> CpuUse(TimeSpan window)
    {
        var before = Snapshot().ToDictionary(e => e.Pid);
        Thread.Sleep(window);
        var result = new List<(string, double)>();
        foreach (var after in Snapshot())
        {
            // The same pid with a different name is a new process that reused the number.
            if (after.Pid == 0 || !before.TryGetValue(after.Pid, out var earlier) || earlier.Name != after.Name) continue;
            result.Add((after.Name, after.CpuTime - earlier.CpuTime));
        }

        return result;
    }

    /// <summary>Memory in use by each process, by program name.</summary>
    // Windows' own memory bookkeeping, not apps you could close; its memory grows exactly when memory runs low.
    private static readonly HashSet<string> NotApps = new(StringComparer.OrdinalIgnoreCase)
    {
        "System", "Secure System", "Registry", "Memory Compression",
    };

    public static List<(string Name, double Amount)> MemoryUse() =>
        Snapshot().Where(e => e.Pid != 0 && !NotApps.Contains(e.Name)).Select(e => (e.Name, (double)e.WorkingSet)).ToList();

    public static Dictionary<int, string> Names()
    {
        var names = new Dictionary<int, string>();
        foreach (var entry in Snapshot()) names[entry.Pid] = entry.Name;
        return names;
    }

    public static List<Entry> Snapshot()
    {
        if (!Environment.Is64BitProcess) return []; // ProcessEntry has the 64-bit layout (Capsule ships only 64-bit builds)
        var size = 1024 * 1024;
        while (size <= MaxBufferBytes)
        {
            var buffer = Marshal.AllocHGlobal(size);
            try
            {
                var status = NtQuerySystemInformation(SystemProcessInformation, buffer, size, out var needed);
                if (status == InfoLengthMismatch)
                {
                    size = Math.Max(size * 2, needed + 256 * 1024); // processes may start while we resize
                    continue;
                }

                return status == 0 ? Read(buffer, Math.Min(needed, size)) : [];
            }
            catch (Exception ex)
            {
                Log.Error("Could not read the process list", ex);
                return [];
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        return [];
    }

    private static List<Entry> Read(IntPtr buffer, int length)
    {
        var list = new List<Entry>(512);
        var entrySize = Marshal.SizeOf<ProcessEntry>();
        var offset = 0;
        while (offset >= 0 && offset + entrySize <= length)
        {
            var entry = Marshal.PtrToStructure<ProcessEntry>(buffer + offset);
            var pid = (int)entry.UniqueProcessId;
            var name = pid == 0 ? "Idle"
                : entry.ImageName.Buffer == IntPtr.Zero ? ""
                : Marshal.PtrToStringUni(entry.ImageName.Buffer, Math.Min(entry.ImageName.Length / 2, 260)) ?? "";
            list.Add(new Entry(pid, name, entry.KernelTime + entry.UserTime, (long)(ulong)entry.WorkingSetSize));

            if (entry.NextEntryOffset == 0) break;
            offset += (int)entry.NextEntryOffset;
        }

        return list;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct UnicodeString
    {
        public ushort Length;
        public ushort MaximumLength;
        public IntPtr Buffer;
    }

    // SYSTEM_PROCESS_INFORMATION, up to the working set (the 64-bit layout; Capsule ships only 64-bit builds).
    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessEntry
    {
        public uint NextEntryOffset;
        public uint NumberOfThreads;
        public long WorkingSetPrivateSize;
        public uint HardFaultCount;
        public uint NumberOfThreadsHighWatermark;
        public ulong CycleTime;
        public long CreateTime;
        public long UserTime;
        public long KernelTime;
        public UnicodeString ImageName;
        public int BasePriority;
        public IntPtr UniqueProcessId;
        public IntPtr InheritedFromUniqueProcessId;
        public uint HandleCount;
        public uint SessionId;
        public UIntPtr UniqueProcessKey;
        public UIntPtr PeakVirtualSize;
        public UIntPtr VirtualSize;
        public uint PageFaultCount;
        public UIntPtr PeakWorkingSetSize;
        public UIntPtr WorkingSetSize;
    }

    [DllImport("ntdll.dll")]
    private static extern int NtQuerySystemInformation(int infoClass, IntPtr buffer, int length, out int returnLength);
}

/// <summary>Performance counters read through PDH (the same source Task Manager uses).</summary>
internal sealed class Pdh : IDisposable
{
    private const uint FormatDouble = 0x00000200;
    private const uint FormatNoCap100 = 0x00008000;
    private const int MoreData = unchecked((int)0x800007D2);
    private const uint MaxArrayBytes = 16 * 1024 * 1024;
    private static bool _loggedTooBig;

    private readonly IntPtr _query;
    private readonly IntPtr _heatQuery;
    private readonly IntPtr _cpuQuery;
    private readonly IntPtr _gpu;
    private readonly IntPtr _thermal;
    private readonly IntPtr _cpu;
    private readonly bool _ok;

    public Pdh()
    {
        try
        {
            _ok = PdhOpenQueryW(null, IntPtr.Zero, out _query) == 0;
            if (!_ok) return;
            // Every engine of every GPU: GpuEngines works out which chip and engine is busiest.
            PdhAddEnglishCounterW(_query, @"\GPU Engine(*)\Utilization Percentage", IntPtr.Zero, out _gpu);
            PdhCollectQueryData(_query);
            if (PdhOpenQueryW(null, IntPtr.Zero, out _heatQuery) == 0)
            {
                PdhAddEnglishCounterW(_heatQuery, @"\Thermal Zone Information(*)\% Passive Limit", IntPtr.Zero, out _thermal);
                PdhCollectQueryData(_heatQuery);
            }

            // Task Manager's CPU figure: it counts boost clocks, so it reads well above plain processor time.
            if (PdhOpenQueryW(null, IntPtr.Zero, out _cpuQuery) == 0
                && PdhAddEnglishCounterW(_cpuQuery, @"\Processor Information(_Total)\% Processor Utility", IntPtr.Zero, out _cpu) == 0)
            {
                PdhCollectQueryData(_cpuQuery);
            }
        }
        catch (Exception ex)
        {
            Log.Error("Performance counters are unavailable", ex);
            _ok = false;
        }
    }

    /// <summary>How busy the busiest GPU is (0 to 100) and how much each process used of its busiest engine.</summary>
    public (double Percent, List<(int Pid, double Value)> Users)? GpuLoad()
    {
        if (!_ok || _gpu == IntPtr.Zero || PdhCollectQueryData(_query) != 0) return null;
        var items = Read(_gpu);
        return items is null ? null : GpuEngines.Busiest(items);
    }

    /// <summary>CPU use as Task Manager shows it (0 to 100) since the last call; null when the counter is missing.</summary>
    public double? CpuUtility()
    {
        if (!_ok || _cpu == IntPtr.Zero || PdhCollectQueryData(_cpuQuery) != 0) return null;
        if (PdhGetFormattedCounterValue(_cpu, FormatDouble, out _, out var value) != 0 || value.Status != 0) return null;
        return Math.Clamp(value.Value, 0, 100);
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
        if (_cpuQuery != IntPtr.Zero) PdhCloseQuery(_cpuQuery);
    }

    private static List<(string Name, double Value)>? Read(IntPtr counter)
    {
        uint size = 0;
        var status = PdhGetFormattedCounterArrayW(counter, FormatDouble | FormatNoCap100, ref size, out _, IntPtr.Zero);
        if (status != MoreData || size == 0) return null;
        if (size > MaxArrayBytes)
        {
            // Every engine of every GPU for every app can add up on a PC with several graphics cards.
            if (!_loggedTooBig) Log.Info($"A performance counter list was too large to read ({size / 1024} KB)");
            _loggedTooBig = true;
            return null;
        }

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
    private static extern int PdhGetFormattedCounterValue(IntPtr counter, uint format, out uint type, out FormattedValue value);

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
