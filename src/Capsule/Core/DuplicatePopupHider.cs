using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Threading;

namespace Capsule.Core;

/// <summary>
/// Some keyboard apps show their own Caps Lock / Num Lock pop-up next to Capsule's. Right after a lock key changes
/// (and only then) this hides that duplicate for a moment. There is no background watcher: it does nothing until
/// Capsule's own lock-key pop-up is about to show, and the app's other alerts (battery and so on) are never touched.
/// </summary>
internal sealed class DuplicatePopupHider : IDisposable
{
    /// <summary>A keyboard app's pop-up: the process that owns it and a piece of its window class.</summary>
    private sealed record KnownPopup(string ProcessName, string ClassContains, string AppName, string SettingsExe);

    private static readonly KnownPopup[] Known =
    [
        new("logioptionsplus_agent", "QWindowIcon", "Logi Options+", "logioptionsplus.exe"),
    ];

    private static readonly TimeSpan ArmFor = TimeSpan.FromMilliseconds(1500);
    private const int MaxPopupSize = 600; // pixels; the pop-ups are small, the apps' main windows are not

    private readonly DispatcherTimer _timer;
    private DateTime _until;

    public DuplicatePopupHider(Dispatcher ui)
    {
        _timer = new DispatcherTimer(DispatcherPriority.Send, ui) { Interval = TimeSpan.FromMilliseconds(30) };
        _timer.Tick += (_, _) =>
        {
            HideNow();
            if (DateTime.UtcNow >= _until) _timer.Stop();
        };
    }

    public bool Enabled { get; set; }

    /// <summary>A lock key just changed: hide a duplicate pop-up now and for the next moment.</summary>
    public void Arm()
    {
        if (!Enabled) return;
        _until = DateTime.UtcNow + ArmFor;
        HideNow();
        _timer.Start();
    }

    public void Dispose() => _timer.Stop();

    /// <summary>The first keyboard app on this PC known to show its own lock-key pop-up, if any.</summary>
    public static (string Name, string? SettingsPath)? DetectApp()
    {
        foreach (var app in Known)
        {
            try
            {
                foreach (var process in Process.GetProcessesByName(app.ProcessName))
                {
                    using (process)
                    {
                        var folder = Path.GetDirectoryName(ImagePath((uint)process.Id) ?? "");
                        var settings = string.IsNullOrEmpty(folder) ? null : Path.Combine(folder, app.SettingsExe);
                        return (app.AppName, settings is not null && File.Exists(settings) ? settings : null);
                    }
                }
            }
            catch
            {
                // Can't look at that process: treat the app as absent.
            }
        }

        return null;
    }

    private static void HideNow()
    {
        EnumWindows((window, _) =>
        {
            try
            {
                if (!IsWindowVisible(window)) return true;
                var className = new StringBuilder(256);
                if (GetClassNameW(window, className, className.Capacity) == 0) return true;
                var app = Known.FirstOrDefault(k => className.ToString().Contains(k.ClassContains, StringComparison.Ordinal));
                if (app is null) return true;
                if (!GetWindowRect(window, out var r) || r.Right - r.Left > MaxPopupSize || r.Bottom - r.Top > MaxPopupSize) return true;

                GetWindowThreadProcessId(window, out var pid);
                var image = ImagePath(pid);
                if (image is null || !string.Equals(Path.GetFileNameWithoutExtension(image), app.ProcessName, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }

                ShowWindow(window, SwHide);
            }
            catch
            {
                // A window that vanished mid-check is fine to skip.
            }

            return true;
        }, IntPtr.Zero);
    }

    private static string? ImagePath(uint pid)
    {
        var handle = OpenProcess(QueryLimitedInformation, false, pid);
        if (handle == IntPtr.Zero) return null;
        try
        {
            var buffer = new StringBuilder(1024);
            var size = buffer.Capacity;
            return QueryFullProcessImageNameW(handle, 0, buffer, ref size) ? buffer.ToString() : null;
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    private const uint QueryLimitedInformation = 0x1000;
    private const int SwHide = 0;

    private delegate bool EnumWindowsProc(IntPtr window, IntPtr data);

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left, Top, Right, Bottom;
    }

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr data);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr window);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassNameW(IntPtr window, StringBuilder name, int size);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr window, out Rect rect);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr window, int command);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool QueryFullProcessImageNameW(IntPtr process, int flags, StringBuilder name, ref int size);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);
}
