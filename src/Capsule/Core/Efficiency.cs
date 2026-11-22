using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace Capsule.Core;

/// <summary>Keeps Capsule light: Windows Efficiency mode, low priority, and handing back unused memory.</summary>
internal static class Efficiency
{
    private const int ProcessPowerThrottling = 4;
    private const uint ThrottleExecutionSpeed = 0x1;

    /// <summary>
    /// Turns on Windows "Efficiency mode" (EcoQoS: the green leaf in Task Manager) and below-normal priority,
    /// so Capsule never competes with games or other apps for CPU time or battery.
    /// </summary>
    public static void Enable()
    {
        try
        {
            using var self = Process.GetCurrentProcess();
            self.PriorityClass = ProcessPriorityClass.BelowNormal;

            var state = new PowerThrottlingState
            {
                Version = 1,
                ControlMask = ThrottleExecutionSpeed,
                StateMask = ThrottleExecutionSpeed,
            };
            SetProcessInformation(self.Handle, ProcessPowerThrottling, ref state, (uint)Marshal.SizeOf<PowerThrottlingState>());
        }
        catch (Exception ex)
        {
            Log.Error("Could not enable efficiency mode", ex);
        }
    }

    /// <summary>Releases memory Capsule isn't using right now (Windows pages it back in if needed).</summary>
    public static void TrimMemory()
    {
        try
        {
            GC.Collect(2, GCCollectionMode.Optimized, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();
            using var self = Process.GetCurrentProcess();
            EmptyWorkingSet(self.Handle);
        }
        catch
        {
            // Best effort.
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PowerThrottlingState
    {
        public uint Version;
        public uint ControlMask;
        public uint StateMask;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetProcessInformation(IntPtr process, int infoClass, ref PowerThrottlingState info, uint size);

    [DllImport("psapi.dll", EntryPoint = "EmptyWorkingSet")]
    private static extern bool EmptyWorkingSet(IntPtr process);
}

/// <summary>Calls back when anything under a registry key changes (Windows notifies us; no polling).</summary>
internal sealed class RegistryChangeWatcher : IDisposable
{
    private const int NotifyName = 0x1;
    private const int NotifyLastSet = 0x4;
    private const int NotifyThreadAgnostic = 0x10000000;

    private readonly RegistryKey _key;
    private readonly AutoResetEvent _signal = new(false);
    private readonly Action _changed;
    private RegisteredWaitHandle? _wait;
    private bool _disposed;

    private RegistryChangeWatcher(RegistryKey key, Action changed)
    {
        _key = key;
        _changed = changed;
    }

    public static RegistryChangeWatcher? TryCreate(string currentUserPath, Action changed)
    {
        try
        {
            var key = Registry.CurrentUser.OpenSubKey(currentUserPath);
            if (key is null) return null;
            var watcher = new RegistryChangeWatcher(key, changed);
            return watcher.Arm() ? watcher : null;
        }
        catch (Exception ex)
        {
            Log.Error("Registry watch failed", ex);
            return null;
        }
    }

    public void Dispose()
    {
        _disposed = true;
        _wait?.Unregister(null);
        _key.Dispose();
        _signal.Dispose();
    }

    private bool Arm()
    {
        if (_disposed) return false;
        var result = RegNotifyChangeKeyValue(_key.Handle, true, NotifyName | NotifyLastSet | NotifyThreadAgnostic,
            _signal.SafeWaitHandle, true);
        if (result != 0) return false;

        _wait = ThreadPool.RegisterWaitForSingleObject(_signal, (_, _) =>
        {
            if (_disposed) return;
            Thread.Sleep(300); // changes come in bursts
            try
            {
                _changed();
            }
            catch (Exception ex)
            {
                Log.Error("Registry change handler failed", ex);
            }

            try
            {
                Arm();
            }
            catch (ObjectDisposedException)
            {
                // Shutting down.
            }
        }, null, Timeout.Infinite, executeOnlyOnce: true);
        return true;
    }

    [DllImport("advapi32.dll")]
    private static extern int RegNotifyChangeKeyValue(SafeRegistryHandle key, bool watchSubtree, int filter, SafeWaitHandle evt, bool asynchronous);
}
